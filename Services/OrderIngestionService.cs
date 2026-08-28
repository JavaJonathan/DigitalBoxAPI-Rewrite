using System.Security.Cryptography;
using DigitalBoxApi.Data;
using DigitalBoxApi.Entities;
using DigitalBoxApi.Models.Orders;
using Microsoft.EntityFrameworkCore;

namespace DigitalBoxApi.Services;

public class OrderIngestionService
{
    private readonly ApplicationDbContext _db;
    private readonly IPackingSlipParser _parser;
    private readonly IPackingSlipStore _slipStore;
    private readonly ILogger<OrderIngestionService> _logger;

    public OrderIngestionService(
        ApplicationDbContext db,
        IPackingSlipParser parser,
        IPackingSlipStore slipStore,
        ILogger<OrderIngestionService> logger)
    {
        _db = db;
        _parser = parser;
        _slipStore = slipStore;
        _logger = logger;
    }

    /// <summary>
    /// Parses one uploaded PDF and, unless it is a byte-for-byte duplicate, creates an Order
    /// (+ line items + packing slip + Created event). Each file is committed in its own
    /// transaction so one bad file does not roll back a whole batch.
    /// </summary>
    public async Task<UploadFileResultModel> IngestAsync(
        string fileName, byte[] bytes, CancellationToken ct)
    {
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var existing = await _db.PackingSlips
            .Where(s => s.Sha256 == sha)
            .Select(s => new { s.Id, OrderId = (Guid?)(s.Order != null ? s.Order.Id : (Guid?)null) })
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            return new UploadFileResultModel
            {
                FileName = fileName,
                Outcome = "duplicate",
                OrderId = existing.OrderId,
                Message = "This packing slip was already uploaded."
            };
        }

        var parsed = _parser.Parse(bytes);

        var now = DateTime.UtcNow;
        var order = new Order
        {
            OrderNumber = parsed.OrderNumber,
            Marketplace = MarketplaceDetector.Detect(parsed.OrderNumber),
            ShipDate = parsed.ShipDate,
            Status = OrderStatus.Open,
            ParseStatus = parsed.Confidence switch
            {
                ParseConfidence.Good => ParseStatus.Parsed,
                ParseConfidence.Partial => ParseStatus.NeedsReview,
                _ => ParseStatus.Failed
            },
            CreatedAt = now,
            UpdatedAt = now
        };

        order.LineItems = parsed.LineItems
            .Select((li, index) => new OrderLineItem
            {
                Title = li.Title,
                Quantity = li.Quantity,
                Sku = li.Sku,
                SortOrder = index
            })
            .ToList();

        order.SearchText = SearchText.Build(order.OrderNumber, order.LineItems);

        order.Events.Add(new OrderEvent
        {
            Type = OrderEventType.Created,
            Detail = parsed.Note ?? $"Parsed {order.LineItems.Count} line item(s).",
            OccurredAt = now
        });

        order.PackingSlip = _slipStore.Create(fileName, "application/pdf", bytes, sha);

        _db.Orders.Add(order);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Failed to persist uploaded slip {FileName}.", fileName);
            _db.ChangeTracker.Clear();
            return new UploadFileResultModel
            {
                FileName = fileName,
                Outcome = "error",
                Message = "Could not save this order (possibly a concurrent duplicate)."
            };
        }

        return new UploadFileResultModel
        {
            FileName = fileName,
            Outcome = "created",
            OrderId = order.Id,
            OrderNumber = order.OrderNumber,
            ParseStatus = order.ParseStatus.ToString(),
            Message = parsed.Note
        };
    }
}
