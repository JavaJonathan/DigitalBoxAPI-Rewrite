using System.Security.Claims;
using DigitalBoxApi.Data;
using DigitalBoxApi.Entities;
using DigitalBoxApi.Models.Orders;
using DigitalBoxApi.Realtime;
using DigitalBoxApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
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
    private readonly IHubContext<PresenceHub, IActivityClient> _hub;
    private readonly ILogger<OrdersController> _logger;

    public OrdersController(
        ApplicationDbContext db,
        OrderIngestionService ingestion,
        IPackingSlipStore slipStore,
        IHubContext<PresenceHub, IActivityClient> hub,
        ILogger<OrdersController> logger)
    {
        _db = db;
        _ingestion = ingestion;
        _slipStore = slipStore;
        _hub = hub;
        _logger = logger;
    }

    // announce: when true (the default, and what a single non-chunked upload sends), broadcast a
    // "someone uploaded N orders" activity popup. The UI sets it false on every batch of a
    // chunked upload and instead calls AnnounceUpload once at the end, so coworkers get one
    // popup rather than one per chunk. A queue-refresh nudge always fires.
    // Admin-only: staff work the queue, admins bring orders into it.
    [HttpPost("upload")]
    [Authorize(Roles = nameof(UserRole.Admin))]
    [RequestSizeLimit(100 * 1024 * 1024)]
    public async Task<ActionResult<UploadResponseModel>> Upload(
        [FromForm] List<IFormFile> files, [FromForm] bool announce = true, CancellationToken ct = default)
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

            // Length is known from the multipart headers, so read straight into a right-sized
            // buffer — no MemoryStream growth + second .ToArray() copy (was ~2x the file in RAM).
            var bytes = new byte[file.Length];
            await using (var stream = file.OpenReadStream())
            {
                await stream.ReadExactlyAsync(bytes, ct);
            }

            var result = await _ingestion.IngestAsync(file.FileName, bytes, ct);
            response.Files.Add(result);
            switch (result.Outcome)
            {
                case "created": response.Created++; break;
                case "duplicate": response.Duplicates++; break;
                default: response.Errors++; break;
            }
        }

        var (actor, actorId) = CurrentActor();
        if (announce)
        {
            await Broadcast("uploaded", response.Created, actor, actorId);
        }
        else
        {
            // Chunked upload: no popup yet, but connected queues still need to re-fetch now.
            await NotifyQueueChanged();
        }

        return Ok(response);
    }

    // Fire the single "someone uploaded N orders" activity popup after a chunked upload has
    // sent all of its batches with announce=false. Best-effort, like every hub push.
    [HttpPost("upload/announce")]
    [Authorize(Roles = nameof(UserRole.Admin))]
    public async Task<IActionResult> AnnounceUpload(AnnounceUploadRequestModel request)
    {
        var (actor, actorId) = CurrentActor();
        await Broadcast("uploaded", request.Created, actor, actorId);
        return NoContent();
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<OrderListItemModel>>> List(
        [FromQuery] string? q,
        [FromQuery] string? marketplace,
        [FromQuery] bool? priority,
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

        if (priority == true)
        {
            query = query.Where(o => o.IsPriority);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var normalized = SearchText.Normalize(q);
            if (normalized.Length > 0)
            {
                query = query.Where(o => EF.Functions.ILike(o.SearchText, $"%{normalized}%"));
            }
        }

        var sortKey = sort.ToLowerInvariant();
        IOrderedQueryable<Order> ordered;
        if (statusEnum == OrderStatus.Open)
        {
            // Priority orders always lead the Open queue, regardless of the chosen sort.
            var byPriority = query.OrderByDescending(o => o.IsPriority);
            ordered = sortKey switch
            {
                "title" => byPriority
                    .ThenBy(o => o.LineItems.OrderBy(li => li.SortOrder).Select(li => li.Title).FirstOrDefault())
                    .ThenBy(o => o.ShipDate ?? DateOnly.MaxValue),
                "created" => byPriority.ThenByDescending(o => o.CreatedAt),
                _ => byPriority.ThenBy(o => o.ShipDate ?? DateOnly.MaxValue).ThenBy(o => o.CreatedAt),
            };
        }
        else
        {
            ordered = sortKey switch
            {
                "title" => query
                    .OrderBy(o => o.LineItems.OrderBy(li => li.SortOrder).Select(li => li.Title).FirstOrDefault())
                    .ThenBy(o => o.ShipDate ?? DateOnly.MaxValue),
                "created" => query.OrderByDescending(o => o.CreatedAt),
                _ => query.OrderByDescending(o => o.ShippedAt ?? o.CancelledAt ?? o.UpdatedAt),
            };
        }

        // Deterministic tiebreaker so pagination can't duplicate or skip rows on a tie.
        query = ordered.ThenBy(o => o.Id);

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
        if (order is null)
        {
            return NotFound();
        }

        var slip = await LoadSlipInfo(order.PackingSlipId, ct);
        return Ok(OrderDetailModel.From(order, slip));
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

    // Admin-only: correcting parsed order fields is an admin task; staff triage via
    // priority/notes and ship/cancel/reopen, which stay open to every signed-in user.
    [HttpPut("{id:guid}")]
    [Authorize(Roles = nameof(UserRole.Admin))]
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

        order.SearchText = SearchText.Build(order.OrderNumber, order.LineItems, order.Notes);
        order.ParseStatus = ParseStatus.Parsed;
        order.UpdatedAt = DateTime.UtcNow;
        order.Events.Add(new OrderEvent
        {
            Type = OrderEventType.Edited,
            Detail = "Order details corrected.",
            OccurredAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);
        await NotifyQueueChanged();

        var reloaded = await LoadDetail(id, ct);
        var slip = await LoadSlipInfo(reloaded!.PackingSlipId, ct);
        return Ok(OrderDetailModel.From(reloaded, slip));
    }

    // Toggle the urgent-triage flag. Works in any status; no timeline event (too noisy).
    [HttpPost("{id:guid}/priority")]
    public async Task<ActionResult<OrderDetailModel>> SetPriority(
        Guid id, SetPriorityRequestModel request, CancellationToken ct)
    {
        var order = await LoadDetail(id, ct);
        if (order is null)
        {
            return NotFound();
        }

        order.IsPriority = request.IsPriority;
        order.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await NotifyQueueChanged();

        var slip = await LoadSlipInfo(order.PackingSlipId, ct);
        return Ok(OrderDetailModel.From(order, slip));
    }

    // Set or clear the operator note. Editable in any status (unlike the full edit, which 409s).
    [HttpPut("{id:guid}/notes")]
    public async Task<ActionResult<OrderDetailModel>> SetNotes(
        Guid id, SetNotesRequestModel request, CancellationToken ct)
    {
        var order = await LoadDetail(id, ct);
        if (order is null)
        {
            return NotFound();
        }

        var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        order.Notes = notes;
        order.SearchText = SearchText.Build(order.OrderNumber, order.LineItems, notes);
        order.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await NotifyQueueChanged();

        var slip = await LoadSlipInfo(order.PackingSlipId, ct);
        return Ok(OrderDetailModel.From(order, slip));
    }

    [HttpPost("ship")]
    public Task<ActionResult<ActionResultModel>> Ship(ShipOrCancelRequestModel request, CancellationToken ct)
        => Transition(request, OrderStatus.Shipped, ct);

    [HttpPost("cancel")]
    public Task<ActionResult<ActionResultModel>> Cancel(ShipOrCancelRequestModel request, CancellationToken ct)
        => Transition(request, OrderStatus.Cancelled, ct);

    // The signed-in user, stamped onto the order and its audit event.
    private (string Name, Guid? Id) CurrentActor()
    {
        var name = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "Unknown";
        return Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? (name, id)
            : (name, null);
    }

    // Push a coworker-activity popup + a queue-changed nudge to every connected browser.
    // Best-effort: a realtime failure must never fail the HTTP action that just succeeded.
    private async Task Broadcast(string verb, int count, string actor, Guid? actorId)
    {
        if (count <= 0)
        {
            return;
        }

        try
        {
            await _hub.Clients.All.Activity(new ActivityEvent(
                Guid.NewGuid(), actorId ?? Guid.Empty, actor, verb, count, DateTime.UtcNow));
            await _hub.Clients.All.QueueChanged();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast '{Verb}' activity over the hub.", verb);
        }
    }

    // Nudge connected queues to re-fetch after a silent edit (no activity popup).
    private async Task NotifyQueueChanged()
    {
        try
        {
            await _hub.Clients.All.QueueChanged();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast a queue change over the hub.");
        }
    }

    private async Task<ActionResult<ActionResultModel>> Transition(
        ShipOrCancelRequestModel request, OrderStatus target, CancellationToken ct)
    {
        var (actor, actorId) = CurrentActor();

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
            order.ActionedByUserId = actorId;
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
                ActorUserId = actorId,
                OccurredAt = now
            });

            result.Updated++;
        }

        await _db.SaveChangesAsync(ct);

        var verb = target == OrderStatus.Shipped ? "shipped" : "cancelled";
        result.Message = result.SkippedIds.Count == 0
            ? $"{result.Updated} order(s) {verb}."
            : $"{result.Updated} order(s) {verb}; {result.SkippedIds.Count} skipped (not open or not found).";

        await Broadcast(verb, result.Updated, actor, actorId);

        return Ok(result);
    }

    // Reopen shipped or cancelled orders back to the Open queue. Direction is inferred from
    // current status; priority and notes survive (unlike the original, which lost them).
    [HttpPost("undo")]
    public async Task<ActionResult<ActionResultModel>> Undo(ShipOrCancelRequestModel request, CancellationToken ct)
    {
        var (actor, actorId) = CurrentActor();

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
            if (order is null || order.Status == OrderStatus.Open)
            {
                result.SkippedIds.Add(id);
                continue;
            }

            var from = order.Status;
            order.Status = OrderStatus.Open;
            order.ActionedBy = null;
            order.ActionedByUserId = null;
            order.ShippedAt = null;
            order.CancelledAt = null;
            order.UpdatedAt = now;

            order.Events.Add(new OrderEvent
            {
                Type = OrderEventType.Reopened,
                Actor = actor,
                ActorUserId = actorId,
                Detail = from == OrderStatus.Shipped ? "Reopened from shipped." : "Reopened from cancelled.",
                OccurredAt = now
            });

            result.Updated++;
        }

        await _db.SaveChangesAsync(ct);

        result.Message = result.SkippedIds.Count == 0
            ? $"{result.Updated} order(s) reopened."
            : $"{result.Updated} order(s) reopened; {result.SkippedIds.Count} skipped (already open or not found).";

        await Broadcast("reopened", result.Updated, actor, actorId);

        return Ok(result);
    }

    private Task<Order?> LoadDetail(Guid id, CancellationToken ct) => _db.Orders
        .Include(o => o.LineItems)
        .Include(o => o.Events)
        .FirstOrDefaultAsync(o => o.Id == id, ct);

    // The slip's display metadata only — never its bytes (those stream from
    // GET /{id}/packing-slip). Keeps the ~80 KB bytea out of every detail/edit response.
    private async Task<PackingSlipInfoModel> LoadSlipInfo(Guid packingSlipId, CancellationToken ct) =>
        await _db.PackingSlips
            .Where(s => s.Id == packingSlipId)
            .Select(s => new PackingSlipInfoModel { Id = s.Id, FileName = s.FileName, ByteSize = s.ByteSize })
            .FirstOrDefaultAsync(ct) ?? new PackingSlipInfoModel();

    private static bool IsPdf(IFormFile file)
    {
        if (file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return file.ContentType is "application/pdf" or "application/x-pdf";
    }
}
