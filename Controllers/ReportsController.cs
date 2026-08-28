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
    private const int MaxCsvRows = 100_000;

    private readonly ApplicationDbContext _db;
    private readonly ILogger<ReportsController> _logger;

    public ReportsController(ApplicationDbContext db, ILogger<ReportsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Cross-references an uploaded inventory CSV against open-order demand. Returns a JSON
    /// preview; the UI builds the download CSV from it. Synchronous — no worker/polling.
    /// </summary>
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

        var openLines = await _db.Orders
            .Where(o => o.Status == OrderStatus.Open)
            .SelectMany(o => o.LineItems)
            .Select(li => new { li.Sku, li.Title, li.Quantity })
            .ToListAsync(ct);

        var openOrderCount = await _db.Orders.CountAsync(o => o.Status == OrderStatus.Open, ct);

        var items = ShippableItemsReport.Build(
            rows,
            openLines.Select(x => new OpenOrderLine(x.Sku, x.Title, x.Quantity)).ToList());

        return Ok(new ShippableItemsResponseModel
        {
            Rows = items.Select(i => new ShippableItemsRowModel
            {
                Title = i.Title,
                Sku = i.Sku,
                OrderedQty = i.OrderedQty,
                OnHandQty = i.OnHandQty,
                ShippableQty = i.ShippableQty
            }).ToList(),
            GeneratedAt = DateTime.UtcNow,
            OpenOrderCount = openOrderCount,
            CsvRowCount = rows.Count,
            MatchedRowCount = items.Count
        });
    }

    private static bool IsCsv(IFormFile file) =>
        file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
        || file.ContentType is "text/csv" or "application/csv" or "application/vnd.ms-excel"
        || file.ContentType is "text/plain";
}
