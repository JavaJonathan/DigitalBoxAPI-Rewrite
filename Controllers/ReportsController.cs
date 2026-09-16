using DigitalBoxApi.Data;
using DigitalBoxApi.Entities;
using DigitalBoxApi.Models.Reports;
using DigitalBoxApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DigitalBoxApi.Controllers;

[ApiController]
[Route("api/reports")]
[Authorize]
public class ReportsController : ControllerBase
{
    private const long MaxCsvBytes = 25 * 1024 * 1024;
    private const int MaxCsvRows = 50_000;

    private readonly ApplicationDbContext _db;
    private readonly ILogger<ReportsController> _logger;

    public ReportsController(ApplicationDbContext db, ILogger<ReportsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    // Cross-references an uploaded inventory CSV against open-order demand and allocates it
    // per order. Returns a JSON preview; the UI builds the download CSV from it. Synchronous,
    // no worker/polling.
    [HttpPost("shippable-orders")]
    [RequestSizeLimit(MaxCsvBytes + 1024 * 1024)]
    public async Task<ActionResult<ShippableOrdersResponseModel>> ShippableOrders(
        [FromForm] IFormFile? file,
        [FromForm] string? skuColumn,
        [FromForm] string? titleColumn,
        [FromForm] string? qtyColumn,
        CancellationToken ct)
    {
        var (rows, rowsError) = await ReadInventoryCsvAsync(file, skuColumn, titleColumn, qtyColumn, ct);
        if (rowsError is not null)
        {
            return rowsError;
        }

        var (orderInfos, openLines, openOrderCount) = await GetOpenOrderDemandAsync(ct);

        var result = ShippableOrdersReport.Build(rows!, orderInfos, openLines);

        return Ok(new ShippableOrdersResponseModel
        {
            Rows = ToRowModels(result.Items),
            UnmatchedDemand = ToUnmatchedModels(result.UnmatchedDemand),
            Orders = result.Orders.Select(o => new ShippableOrderRowModel
            {
                OrderId = o.OrderId,
                OrderNumber = o.OrderNumber,
                Marketplace = o.Marketplace,
                IsPriority = o.IsPriority,
                LineCount = o.LineCount,
                CoveredLineCount = o.CoveredLineCount,
                Status = o.Status,
                ShortLines = o.ShortLines.Select(s => new ShippableOrderShortLineModel
                {
                    Title = s.Title,
                    Sku = s.Sku,
                    OrderedQty = s.OrderedQty,
                    AvailableQty = s.AvailableQty
                }).ToList()
            }).ToList(),
            GeneratedAt = DateTime.UtcNow,
            OpenOrderCount = openOrderCount,
            CsvRowCount = rows!.Count,
            MatchedRowCount = result.Items.Count,
            OrdersShippable = result.OrdersShippable,
            OrdersPartial = result.OrdersPartial,
            OrdersNeedsCheck = result.OrdersNeedsCheck,
            UnitsShippable = result.UnitsShippable
        });
    }

    // The original, item-centric report: aggregate demand vs. on-hand per SKU, no order
    // breakdown. See ShippableOrders above for the order-specific counterpart; both share the
    // same matching engine (InventoryMatching), so their numbers always agree.
    [HttpPost("shippable-items")]
    [RequestSizeLimit(MaxCsvBytes + 1024 * 1024)]
    public async Task<ActionResult<ShippableItemsResponseModel>> ShippableItems(
        [FromForm] IFormFile? file,
        [FromForm] string? skuColumn,
        [FromForm] string? titleColumn,
        [FromForm] string? qtyColumn,
        CancellationToken ct)
    {
        var (rows, rowsError) = await ReadInventoryCsvAsync(file, skuColumn, titleColumn, qtyColumn, ct);
        if (rowsError is not null)
        {
            return rowsError;
        }

        var (_, openLines, openOrderCount) = await GetOpenOrderDemandAsync(ct);

        var result = ShippableItemsReport.Build(rows!, openLines);

        return Ok(new ShippableItemsResponseModel
        {
            Rows = ToRowModels(result.Items),
            UnmatchedDemand = ToUnmatchedModels(result.UnmatchedDemand),
            GeneratedAt = DateTime.UtcNow,
            OpenOrderCount = openOrderCount,
            CsvRowCount = rows!.Count,
            MatchedRowCount = result.Items.Count,
            UnitsShippable = result.UnitsShippable
        });
    }

    private async Task<(List<InventoryRow>? Rows, ActionResult? Error)> ReadInventoryCsvAsync(
        IFormFile? file, string? skuColumn, string? titleColumn, string? qtyColumn, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return (null, BadRequest(new { message = "No CSV file was uploaded." }));
        }

        if (file.Length > MaxCsvBytes)
        {
            return (null, BadRequest(new { message = "The CSV file exceeds 25 MB." }));
        }

        if (!IsCsv(file))
        {
            return (null, BadRequest(new { message = "Upload a .csv file." }));
        }

        if (string.IsNullOrWhiteSpace(skuColumn)
            || string.IsNullOrWhiteSpace(titleColumn)
            || string.IsNullOrWhiteSpace(qtyColumn))
        {
            return (null, BadRequest(new { message = "Choose the SKU, product-title, and on-hand-quantity columns." }));
        }

        try
        {
            await using var stream = file.OpenReadStream();

            // Content check: extension / Content-Type are client-controlled. A NUL byte in the
            // head means this is a binary file (xlsx, image, …), not a text CSV.
            var head = new byte[(int)Math.Min(8192, file.Length)];
            var read = await stream.ReadAsync(head, ct);
            if (Array.IndexOf(head, (byte)0, 0, read) >= 0)
            {
                return (null, BadRequest(new { message = "That doesn't look like a text CSV file." }));
            }

            stream.Position = 0;
            var rows = InventoryCsv.ReadRows(stream, skuColumn, titleColumn, qtyColumn, MaxCsvRows);
            return (rows, null);
        }
        catch (InvalidOperationException ex)
        {
            return (null, BadRequest(new { message = ex.Message }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read the inventory CSV.");
            return (null, BadRequest(new { message = "The CSV could not be read." }));
        }
    }

    private async Task<(List<OpenOrderInfo> Orders, List<OpenOrderLine> Lines, int OpenOrderCount)> GetOpenOrderDemandAsync(
        CancellationToken ct)
    {
        var openOrders = await _db.Orders
            .Where(o => o.Status == OrderStatus.Open)
            .Select(o => new
            {
                o.Id,
                o.OrderNumber,
                o.Marketplace,
                o.IsPriority,
                o.ShipDate,
                o.CreatedAt,
                Lines = o.LineItems
                    .Select(li => new { li.Sku, li.Title, li.Quantity })
                    .ToList()
            })
            .ToListAsync(ct);

        var orderInfos = openOrders
            .Select(o => new OpenOrderInfo(
                o.Id, o.OrderNumber, o.Marketplace.ToString(), o.IsPriority, o.ShipDate, o.CreatedAt))
            .ToList();

        var openLines = openOrders
            .SelectMany(o => o.Lines.Select(li => new OpenOrderLine(o.Id, li.Sku, li.Title, li.Quantity)))
            .ToList();

        return (orderInfos, openLines, openOrders.Count);
    }

    private static List<ShippableItemsRowModel> ToRowModels(IReadOnlyList<ShippableItem> items) =>
        items.Select(i => new ShippableItemsRowModel
        {
            Title = i.Title,
            Sku = i.Sku,
            OrderedQty = i.OrderedQty,
            OnHandQty = i.OnHandQty,
            ShippableQty = i.ShippableQty,
            ShortQty = i.ShortQty,
            Coverage = i.Coverage
        }).ToList();

    private static List<UnmatchedDemandRowModel> ToUnmatchedModels(IReadOnlyList<UnmatchedDemand> unmatched) =>
        unmatched.Select(u => new UnmatchedDemandRowModel
        {
            Sku = u.Sku,
            Title = u.Title,
            OrderedQty = u.OrderedQty,
            OrderCount = u.OrderCount
        }).ToList();

    private static bool IsCsv(IFormFile file) =>
        file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
        || file.ContentType is "text/csv" or "application/csv" or "application/vnd.ms-excel"
        || file.ContentType is "text/plain";
}
