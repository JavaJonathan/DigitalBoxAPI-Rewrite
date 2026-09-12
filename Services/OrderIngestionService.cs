using System.Security.Cryptography;
using System.Text;
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

    // Parses one uploaded PDF, which may contain more than one order (a combined
    // carrier/marketplace batch export), and creates an Order (+ line items + packing slip +
    // Created event) for each detected order that isn't a duplicate. Each detected order is
    // committed in its own transaction so one bad order does not roll back the rest of the same
    // file, the same way each uploaded file is already its own unit within a batch.
    //
    // No whole-file pre-check: splitting a combined PDF into per-order PDFs (PdfDocumentBuilder)
    // is not byte-deterministic between separate uploads (same pages in, different bytes out,
    // confirmed empirically), so byte-hashing a split file can't detect a re-uploaded combined
    // batch. Dedup instead keys on each segment's own parsed content (see ComputeDedupeHash),
    // which is what "the same order" actually means here and is stable across re-splits/re-exports.
    public async Task<List<UploadFileResultModel>> IngestAsync(
        string fileName, byte[] bytes, CancellationToken ct)
    {
        var segments = _parser.Parse(bytes);
        var results = new List<UploadFileResultModel>(segments.Count);
        var now = DateTime.UtcNow;

        foreach (var segment in segments)
        {
            var dedupeHash = ComputeDedupeHash(segment.Slip, segment.PdfBytes);

            var existingSegment = await FindExistingBySha(dedupeHash, ct);
            if (existingSegment is not null)
            {
                results.Add(DuplicateResult(fileName, existingSegment.OrderId));
                continue;
            }

            var order = BuildOrder(segment.Slip, segment.PdfBytes, fileName, dedupeHash, now);
            _db.Orders.Add(order);

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "Failed to persist an order from uploaded slip {FileName}.", fileName);
                _db.ChangeTracker.Clear();
                results.Add(new UploadFileResultModel
                {
                    FileName = fileName,
                    Outcome = "error",
                    Message = "Could not save this order (possibly a concurrent duplicate)."
                });
                continue;
            }

            results.Add(new UploadFileResultModel
            {
                FileName = fileName,
                Outcome = "created",
                OrderId = order.Id,
                OrderNumber = order.OrderNumber,
                ParseStatus = order.ParseStatus.ToString(),
                Message = segment.Slip.Note
            });

            // Keep the tracker flat across what can be many segments from one combined file.
            _db.ChangeTracker.Clear();
        }

        return results;
    }

    // Keys duplicate detection on what a "duplicate order" actually means, not on the exact
    // stored bytes: when a segment parsed to a real order number, hash its own content (order
    // number + ship date + line items) so the same order is recognized as a repeat regardless of
    // how it was re-split or re-exported. When parsing failed (no order number), there is nothing
    // content-meaningful to key on, so fall back to hashing that segment's own bytes -- this
    // keeps distinct unparseable uploads from colliding onto one shared "empty" fingerprint.
    private static string ComputeDedupeHash(ParsedSlip slip, byte[] pdfBytes)
    {
        if (slip.OrderNumber.Length == 0)
        {
            return Convert.ToHexString(SHA256.HashData(pdfBytes)).ToLowerInvariant();
        }

        var fingerprint = new StringBuilder()
            .Append(slip.OrderNumber).Append('|').Append(slip.ShipDate);

        foreach (var li in slip.LineItems)
        {
            fingerprint.Append('|').Append(li.Title).Append(':').Append(li.Quantity).Append(':').Append(li.Sku);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.ToString()))).ToLowerInvariant();
    }

    private sealed record ExistingSlip(Guid? OrderId);

    private async Task<ExistingSlip?> FindExistingBySha(string sha, CancellationToken ct)
    {
        var existing = await _db.PackingSlips
            .Where(s => s.Sha256 == sha)
            .Select(s => new { s.Id, OrderId = (Guid?)(s.Order != null ? s.Order.Id : (Guid?)null) })
            .FirstOrDefaultAsync(ct);

        return existing is null ? null : new ExistingSlip(existing.OrderId);
    }

    private static UploadFileResultModel DuplicateResult(string fileName, Guid? orderId) => new()
    {
        FileName = fileName,
        Outcome = "duplicate",
        OrderId = orderId,
        Message = "This packing slip was already uploaded."
    };

    private Order BuildOrder(ParsedSlip parsed, byte[] pdfBytes, string fileName, string sha, DateTime now)
    {
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

        order.PackingSlip = _slipStore.Create(fileName, "application/pdf", pdfBytes, sha);

        return order;
    }
}
