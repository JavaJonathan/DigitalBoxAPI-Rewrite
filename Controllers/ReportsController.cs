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

    // Cross-references an uploaded inventory CSV against open-order demand. Returns a JSON
    // preview; the UI builds the download CSV from it. Synchronous — no worker/polling.
    [HttpPost("shippable-items")]
    [RequestSizeLimit(MaxCsvBytes + 1024 * 1024)]
    public async Task<ActionResult<ShippableItemsResponseModel>> ShippableItems(
        [FromForm] IFormFile? file,
        [FromForm] string? skuColumn,
        [FromForm] string? titleColumn,
        [FromForm] string? qtyColumn,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { message = "No CSV file was uploaded." });
        }

        if (file.Length > MaxCsvBytes)
        {
            return BadRequest(new { message = "The CSV file exceeds 25 MB." });
        }

        if (!IsCsv(file))
        {
            return BadRequest(new { message = "Upload a .csv file." });
        }

        if (string.IsNullOrWhiteSpace(skuColumn)
            || string.IsNullOrWhiteSpace(titleColumn)
            || string.IsNullOrWhiteSpace(qtyColumn))
        {
            return BadRequest(new { message = "Choose the SKU, product-title, and on-hand-quantity columns." });
        }

        List<InventoryRow> rows;
        try
        {
            await using var stream = file.OpenReadStream();
            rows = InventoryCsv.ReadRows(stream, skuColumn, titleColumn, qtyColumn, MaxCsvRows);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read the inventory CSV.");
            return BadRequest(new { message = "The CSV could not be read." });
        }

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

        var result = ShippableItemsReport.Build(rows, orderInfos, openLines);

        return Ok(new ShippableItemsResponseModel
        {
            Rows = result.Items.Select(i => new ShippableItemsRowModel
            {
                Title = i.Title,
                Sku = i.Sku,
                OrderedQty = i.OrderedQty,
                OnHandQty = i.OnHandQty,
                ShippableQty = i.ShippableQty,
                ShortQty = i.ShortQty,
                Coverage = i.Coverage
            }).ToList(),
            UnmatchedDemand = result.UnmatchedDemand.Select(u => new UnmatchedDemandRowModel
            {
                Sku = u.Sku,
                Title = u.Title,
                OrderedQty = u.OrderedQty,
                OrderCount = u.OrderCount
            }).ToList(),
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
            OpenOrderCount = openOrders.Count,
            CsvRowCount = rows.Count,
            MatchedRowCount = result.Items.Count,
            OrdersShippable = result.OrdersShippable,
            OrdersPartial = result.OrdersPartial,
            OrdersBlocked = result.OrdersBlocked,
            OrdersNeedsCheck = result.OrdersNeedsCheck,
            UnitsShippable = result.UnitsShippable
        });
    }

    private static bool IsCsv(IFormFile file) =>
        file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
        || file.ContentType is "text/csv" or "application/csv" or "application/vnd.ms-excel"
        || file.ContentType is "text/plain";
}
