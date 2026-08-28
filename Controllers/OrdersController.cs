using DigitalBoxApi.Data;
using DigitalBoxApi.Entities;
using DigitalBoxApi.Models.Orders;
using DigitalBoxApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DigitalBoxApi.Controllers;

[ApiController]
[Route("api/orders")]
[Authorize]
public class OrdersController : ControllerBase
{
    private const int MaxUploadFiles = 50;
    private const long MaxFileBytes = 15 * 1024 * 1024;

    private readonly ApplicationDbContext _db;
    private readonly OrderIngestionService _ingestion;
    private readonly IPackingSlipStore _slipStore;
    private readonly ILogger<OrdersController> _logger;

    public OrdersController(
        ApplicationDbContext db,
        OrderIngestionService ingestion,
        IPackingSlipStore slipStore,
        ILogger<OrdersController> logger)
    {
        _db = db;
        _ingestion = ingestion;
        _slipStore = slipStore;
        _logger = logger;
    }

    [HttpPost("upload")]
    [RequestSizeLimit(800 * 1024 * 1024)]
    public async Task<ActionResult<UploadResponseModel>> Upload(
        [FromForm] List<IFormFile> files, CancellationToken ct)
    {
        if (files is null || files.Count == 0)
        {
            return BadRequest(new { message = "No files were uploaded." });
        }

        if (files.Count > MaxUploadFiles)
        {
            return BadRequest(new { message = $"Upload at most {MaxUploadFiles} files at a time." });
        }

        var response = new UploadResponseModel();

        foreach (var file in files)
        {
            if (file.Length == 0)
            {
                response.Errors++;
                response.Files.Add(new UploadFileResultModel
                {
                    FileName = file.FileName, Outcome = "error", Message = "Empty file."
                });
                continue;
            }

            if (file.Length > MaxFileBytes)
            {
                response.Errors++;
                response.Files.Add(new UploadFileResultModel
                {
                    FileName = file.FileName, Outcome = "error", Message = "File exceeds 15 MB."
                });
                continue;
            }

            if (!IsPdf(file))
            {
                response.Errors++;
                response.Files.Add(new UploadFileResultModel
                {
                    FileName = file.FileName, Outcome = "error", Message = "Not a PDF."
                });
                continue;
            }

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();

            var result = await _ingestion.IngestAsync(file.FileName, bytes, ct);
            response.Files.Add(result);
            switch (result.Outcome)
            {
                case "created": response.Created++; break;
                case "duplicate": response.Duplicates++; break;
                default: response.Errors++; break;
            }
        }

        return Ok(response);
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<OrderListItemModel>>> List(
        [FromQuery] string? q,
        [FromQuery] string? marketplace,
        [FromQuery] string status = nameof(OrderStatus.Open),
        [FromQuery] string sort = "shipDate",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (!Enum.TryParse<OrderStatus>(status, ignoreCase: true, out var statusEnum))
        {
            return BadRequest(new { message = $"Unknown status '{status}'." });
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _db.Orders
            .Include(o => o.LineItems)
            .Where(o => o.Status == statusEnum);

        if (!string.IsNullOrWhiteSpace(marketplace)
            && Enum.TryParse<Marketplace>(marketplace, ignoreCase: true, out var marketplaceEnum))
        {
            query = query.Where(o => o.Marketplace == marketplaceEnum);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var normalized = SearchText.Normalize(q);
            if (normalized.Length > 0)
            {
                query = query.Where(o => EF.Functions.ILike(o.SearchText, $"%{normalized}%"));
            }
        }

        query = sort.ToLowerInvariant() switch
        {
            "title" => query
                .OrderBy(o => o.LineItems.OrderBy(li => li.SortOrder).Select(li => li.Title).FirstOrDefault())
                .ThenBy(o => o.ShipDate ?? DateOnly.MaxValue),
            "created" => query.OrderByDescending(o => o.CreatedAt),
            _ => statusEnum == OrderStatus.Open
                ? query.OrderBy(o => o.ShipDate ?? DateOnly.MaxValue).ThenBy(o => o.CreatedAt)
                : query.OrderByDescending(o => o.ShippedAt ?? o.CancelledAt ?? o.UpdatedAt)
        };

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new PagedResult<OrderListItemModel>
        {
            Items = items.Select(OrderListItemModel.FromEntity).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<OrderDetailModel>> Get(Guid id, CancellationToken ct)
    {
        var order = await LoadDetail(id, ct);
        return order is null ? NotFound() : Ok(OrderDetailModel.From(order));
    }

    [HttpGet("{id:guid}/packing-slip")]
    public async Task<IActionResult> PackingSlip(Guid id, CancellationToken ct)
    {
        var slip = await _db.Orders
            .Where(o => o.Id == id)
            .Select(o => new { o.PackingSlipId, o.PackingSlip.FileName })
            .FirstOrDefaultAsync(ct);

        if (slip is null)
        {
            return NotFound();
        }

        var bytes = await _slipStore.GetContentAsync(slip.PackingSlipId, ct);
        if (bytes is null)
        {
            return NotFound();
        }

        Response.Headers.ContentDisposition = $"inline; filename=\"{slip.FileName}\"";
        return File(bytes, "application/pdf");
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<OrderDetailModel>> Update(
        Guid id, UpdateOrderRequestModel request, CancellationToken ct)
    {
        if (!Enum.TryParse<Marketplace>(request.Marketplace, ignoreCase: true, out var marketplace))
        {
            return BadRequest(new { message = $"Unknown marketplace '{request.Marketplace}'." });
        }

        var order = await _db.Orders
            .Include(o => o.LineItems)
            .Include(o => o.Events)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

        if (order is null)
        {
            return NotFound();
        }

        if (order.Status != OrderStatus.Open)
        {
            return Conflict(new { message = "Only open orders can be edited." });
        }

        order.OrderNumber = request.OrderNumber.Trim();
        order.Marketplace = marketplace;
        order.ShipDate = request.ShipDate;

        _db.OrderLineItems.RemoveRange(order.LineItems);
        order.LineItems.Clear();
        foreach (var (li, index) in request.LineItems.Select((li, index) => (li, index)))
        {
            order.LineItems.Add(new OrderLineItem
            {
                Title = li.Title.Trim(),
                Quantity = li.Quantity,
                Sku = string.IsNullOrWhiteSpace(li.Sku) ? null : li.Sku.Trim(),
                SortOrder = index
            });
        }

        order.SearchText = SearchText.Build(order.OrderNumber, order.LineItems);
        order.ParseStatus = ParseStatus.Parsed;
        order.UpdatedAt = DateTime.UtcNow;
        order.Events.Add(new OrderEvent
        {
            Type = OrderEventType.Edited,
            Detail = "Order details corrected.",
            OccurredAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);

        var reloaded = await LoadDetail(id, ct);
        return Ok(OrderDetailModel.From(reloaded!));
    }

    [HttpPost("ship")]
    public Task<ActionResult<ActionResultModel>> Ship(ShipOrCancelRequestModel request, CancellationToken ct)
        => Transition(request, OrderStatus.Shipped, ct);

    [HttpPost("cancel")]
    public Task<ActionResult<ActionResultModel>> Cancel(ShipOrCancelRequestModel request, CancellationToken ct)
        => Transition(request, OrderStatus.Cancelled, ct);

    private async Task<ActionResult<ActionResultModel>> Transition(
        ShipOrCancelRequestModel request, OrderStatus target, CancellationToken ct)
    {
        var actor = request.ActionedBy.Trim();
        if (actor.Length == 0)
        {
            return BadRequest(new { message = "Your name is required." });
        }

        var ids = request.OrderIds.Distinct().ToList();
        var orders = await _db.Orders
            .Include(o => o.Events)
            .Where(o => ids.Contains(o.Id))
            .ToListAsync(ct);

        var result = new ActionResultModel();
        var now = DateTime.UtcNow;

        foreach (var id in ids)
        {
            var order = orders.FirstOrDefault(o => o.Id == id);
            if (order is null || order.Status != OrderStatus.Open)
            {
                result.SkippedIds.Add(id);
                continue;
            }

            order.Status = target;
            order.ActionedBy = actor;
            order.UpdatedAt = now;
            if (target == OrderStatus.Shipped)
            {
                order.ShippedAt = now;
            }
            else
            {
                order.CancelledAt = now;
            }

            order.Events.Add(new OrderEvent
            {
                Type = target == OrderStatus.Shipped ? OrderEventType.Shipped : OrderEventType.Cancelled,
                Actor = actor,
                OccurredAt = now
            });

            result.Updated++;
        }

        await _db.SaveChangesAsync(ct);

        var verb = target == OrderStatus.Shipped ? "shipped" : "cancelled";
        result.Message = result.SkippedIds.Count == 0
            ? $"{result.Updated} order(s) {verb}."
            : $"{result.Updated} order(s) {verb}; {result.SkippedIds.Count} skipped (not open or not found).";

        return Ok(result);
    }

    private Task<Order?> LoadDetail(Guid id, CancellationToken ct) => _db.Orders
        .Include(o => o.LineItems)
        .Include(o => o.Events)
        .Include(o => o.PackingSlip)
        .FirstOrDefaultAsync(o => o.Id == id, ct);

    private static bool IsPdf(IFormFile file)
    {
        if (file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return file.ContentType is "application/pdf" or "application/x-pdf";
    }
}
