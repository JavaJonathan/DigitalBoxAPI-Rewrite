using System.Security.Claims;
using DigitalBoxApi.Data;
using DigitalBoxApi.Entities;
using DigitalBoxApi.Filters;
using DigitalBoxApi.Models.Lookup;
using DigitalBoxApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace DigitalBoxApi.Controllers;

// Admin order-lookup for the customer-service team: "where's this order?" answered against the
// live DigitalBox DB, the uploaded In-Stock / Purchase-Order reference lists, and ShipStation.
// Replaces the standalone CustomerServiceApp. Admin-only AND hidden — a non-admin gets 404
// (see HideFromNonAdmins), so the feature reads as non-existent rather than forbidden.
[ApiController]
[Route("api/lookup")]
[Authorize]
[HideFromNonAdmins]
[EnableRateLimiting("lookup")]
public class LookupController : ControllerBase
{
    private const long MaxCsvBytes = 25 * 1024 * 1024;
    private const int MaxCsvRows = 200_000;

    private readonly ApplicationDbContext _db;
    private readonly LookupService _lookup;
    private readonly ILogger<LookupController> _logger;

    public LookupController(
        ApplicationDbContext db, LookupService lookup, ILogger<LookupController> logger)
    {
        _db = db;
        _lookup = lookup;
        _logger = logger;
    }

    [HttpGet("order")]
    public async Task<ActionResult<LookupResultModel>> LookupOrder(
        [FromQuery] LookupOrderQueryModel query, CancellationToken ct)
    {
        var result = await _lookup.LookupAsync(query.OrderNumber, ct);
        return Ok(result);
    }

    [HttpGet("inventory")]
    public async Task<ActionResult<InventoryStatusModel>> InventoryStatus(CancellationToken ct)
    {
        var uploads = await _db.InventoryUploads
            .Select(u => new { u.Kind, u.FileName, u.RowCount, u.UploadedAt, u.UploadedBy })
            .ToListAsync(ct);

        InventorySnapshotModel? Snapshot(InventoryKind kind)
        {
            var u = uploads.FirstOrDefault(x => x.Kind == kind);
            return u is null ? null : new InventorySnapshotModel
            {
                FileName = u.FileName,
                RowCount = u.RowCount,
                UploadedAt = u.UploadedAt,
                UploadedBy = u.UploadedBy
            };
        }

        return Ok(new InventoryStatusModel
        {
            InStock = Snapshot(InventoryKind.InStock),
            PurchaseOrders = Snapshot(InventoryKind.PurchaseOrder)
        });
    }

    // Replace the current snapshot for one kind. In-Stock lists need all three column mappings;
    // Purchase-Order lists only need SKU.
    [HttpPost("inventory")]
    [RequestSizeLimit(MaxCsvBytes + 1024 * 1024)]
    public async Task<ActionResult<InventoryStatusModel>> UploadInventory(
        [FromForm] IFormFile? file,
        [FromForm] string? kind,
        [FromForm] string? skuColumn,
        [FromForm] string? titleColumn,
        [FromForm] string? qtyColumn,
        CancellationToken ct)
    {
        if (ParseKind(kind) is not { } inventoryKind)
        {
            return BadRequest(new { message = "Choose which list this file is (in stock or purchase orders)." });
        }

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

        if (string.IsNullOrWhiteSpace(skuColumn))
        {
            return BadRequest(new { message = "Choose the SKU column." });
        }

        var wantsAllColumns = inventoryKind == InventoryKind.InStock;
        if (wantsAllColumns && (string.IsNullOrWhiteSpace(titleColumn) || string.IsNullOrWhiteSpace(qtyColumn)))
        {
            return BadRequest(new { message = "Choose the SKU, product-title, and on-hand-quantity columns." });
        }

        List<InventoryRow> rows;
        try
        {
            await using var stream = file.OpenReadStream();

            // Content check: extension / Content-Type are client-controlled. A NUL byte in the
            // head means this is a binary file (xlsx, image, …), not a text CSV.
            var head = new byte[(int)Math.Min(8192, file.Length)];
            var read = await stream.ReadAsync(head, ct);
            if (Array.IndexOf(head, (byte)0, 0, read) >= 0)
            {
                return BadRequest(new { message = "That doesn't look like a text CSV file." });
            }

            stream.Position = 0;
            rows = wantsAllColumns
                ? InventoryCsv.ReadRows(stream, skuColumn, titleColumn, qtyColumn, MaxCsvRows)
                : InventoryCsv.ReadRows(stream, skuColumn, MaxCsvRows);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read an uploaded {Kind} inventory CSV.", inventoryKind);
            return BadRequest(new { message = "The CSV could not be read." });
        }

        var items = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Sku))
            .Select(r => new InventoryItem
            {
                Kind = inventoryKind,
                Sku = r.Sku.Trim().ToUpperInvariant(),
                SkuRaw = r.Sku.Trim(),
                Title = r.Title,
                OnHand = r.OnHand
            })
            .ToList();

        var (actor, actorId) = CurrentActor();

        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            await _db.InventoryItems.Where(i => i.Kind == inventoryKind).ExecuteDeleteAsync(ct);
            _db.InventoryItems.AddRange(items);

            var header = await _db.InventoryUploads.FirstOrDefaultAsync(u => u.Kind == inventoryKind, ct);
            if (header is null)
            {
                header = new InventoryUpload { Kind = inventoryKind };
                _db.InventoryUploads.Add(header);
            }

            header.FileName = file.FileName;
            header.RowCount = items.Count;
            header.UploadedAt = DateTime.UtcNow;
            header.UploadedBy = actor;
            header.UploadedByUserId = actorId;

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        _logger.LogInformation(
            "{Kind} inventory list replaced ({RowCount} SKUs) by {ActorId}.",
            inventoryKind, items.Count, actorId);

        return await InventoryStatus(ct);
    }

    // Accepts the UI's wire values ("inStock" / "purchaseOrders") as well as the enum names.
    private static InventoryKind? ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "instock" => InventoryKind.InStock,
        "purchaseorders" or "purchaseorder" => InventoryKind.PurchaseOrder,
        _ => null
    };

    private (string Name, Guid? Id) CurrentActor()
    {
        var name = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "Unknown";
        return Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? (name, id)
            : (name, null);
    }

    private static bool IsCsv(IFormFile file) =>
        file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
        || file.ContentType is "text/csv" or "application/csv" or "application/vnd.ms-excel"
        || file.ContentType is "text/plain";
}
