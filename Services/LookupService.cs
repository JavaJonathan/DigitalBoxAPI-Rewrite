using DigitalBoxApi.Data;
using DigitalBoxApi.Entities;
using DigitalBoxApi.Models.Lookup;
using Microsoft.EntityFrameworkCore;

namespace DigitalBoxApi.Services;

// The admin order-lookup: find an order in the DigitalBox DB, cross-reference its line items
// against the uploaded In-Stock / Purchase-Order lists, and (when configured) enrich with live
// ShipStation data. Port of CustomerServiceApp's OrderSearch.jsx + ShipStationOrderLookup.jsx
// + utils/skuLookup.js.
public class LookupService
{
    // ShipStation order statuses that mean "not yet shipped": the only ones where a per-item
    // in-stock / pre-order badge is meaningful (old AWAITING_STATUSES).
    private static readonly HashSet<string> AwaitingStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "awaiting_shipment", "awaiting_payment", "pending_fulfillment"
    };

    private readonly ApplicationDbContext _db;
    private readonly ShipStationClient _shipStation;

    public LookupService(ApplicationDbContext db, ShipStationClient shipStation)
    {
        _db = db;
        _shipStation = shipStation;
    }

    public async Task<LookupResultModel> LookupAsync(string orderNumber, CancellationToken ct)
    {
        var trimmed = orderNumber.Trim();
        var lower = trimmed.ToLowerInvariant();

        var matches = await _db.Orders
            .Include(o => o.LineItems)
            .Where(o => o.OrderNumber.ToLower() == lower)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(ct);

        // Fire the ShipStation call alongside the DB read; we want tracking / ship-to even when
        // the order is already in the DB (the DB stores neither).
        var shipStationLookup = await _shipStation.LookupAsync(trimmed, ct);

        var result = new LookupResultModel
        {
            OrderNumber = trimmed,
            ShipStationConfigured = _shipStation.IsConfigured,
            ShipStationError = shipStationLookup.Error
        };

        var dbOrder = matches.FirstOrDefault();
        var ssOrder = shipStationLookup.Order;

        if (dbOrder is not null)
        {
            result.Found = true;
            result.Source = "DigitalBox";
            result.DigitalBox = await BuildDigitalBoxBlockAsync(dbOrder, matches.Count, ct);
            if (ssOrder is not null)
            {
                result.ShipStation = await BuildShipStationBlockAsync(ssOrder, ct);
            }

            return result;
        }

        if (ssOrder is not null)
        {
            result.Found = true;
            result.Source = "ShipStation";
            result.ShipStation = await BuildShipStationBlockAsync(ssOrder, ct);
            return result;
        }

        result.Found = false;
        return result;
    }

    private async Task<LookupDigitalBoxModel> BuildDigitalBoxBlockAsync(
        Order order, int duplicateCount, CancellationToken ct)
    {
        var isOpen = order.Status == OrderStatus.Open;
        var badges = isOpen
            ? await BadgeAsync(order.LineItems.Select(li => li.Sku), ct)
            : new Dictionary<string, string>();

        var lineItems = order.LineItems
            .OrderBy(li => li.SortOrder)
            .Select(li => new LookupLineItemModel
            {
                Title = li.Title,
                Quantity = li.Quantity,
                Sku = li.Sku,
                InventoryStatus = isOpen ? Badge(badges, li.Sku) : null
            })
            .ToList();

        var anyInStock = lineItems.Any(li => li.InventoryStatus == InventoryLineStatus.InStock);

        return new LookupDigitalBoxModel
        {
            OrderId = order.Id,
            Status = order.Status.ToString(),
            Marketplace = order.Marketplace.ToString(),
            ShipDate = order.ShipDate,
            IsPriority = order.IsPriority,
            Notes = order.Notes,
            ParseStatus = order.ParseStatus.ToString(),
            Summary = order.Status switch
            {
                OrderStatus.Shipped => "Shipped",
                OrderStatus.Cancelled => "Cancelled",
                _ => anyInStock ? "Unshipped - In Stock" : "Awaiting Shipment in Digital Box"
            },
            LineItems = lineItems,
            DuplicateCount = duplicateCount
        };
    }

    private async Task<LookupShipStationModel> BuildShipStationBlockAsync(
        ShipStationOrder order, CancellationToken ct)
    {
        var isAwaiting = AwaitingStatuses.Contains(order.OrderStatus);
        var badges = isAwaiting
            ? await BadgeAsync(order.Items.Select(i => i.Sku), ct)
            : new Dictionary<string, string>();

        return new LookupShipStationModel
        {
            OrderStatus = order.OrderStatus,
            OrderDate = order.OrderDate,
            TrackingNumber = order.TrackingNumber,
            Carrier = order.Carrier,
            ShipTo = order.ShipTo is null ? null : new LookupShipToModel
            {
                Name = order.ShipTo.Name,
                Street1 = order.ShipTo.Street1,
                Street2 = order.ShipTo.Street2,
                City = order.ShipTo.City,
                State = order.ShipTo.State,
                PostalCode = order.ShipTo.PostalCode,
                Country = order.ShipTo.Country
            },
            Items = order.Items.Select(i => new LookupLineItemModel
            {
                Title = i.Name,
                Quantity = i.Quantity,
                Sku = i.Sku,
                InventoryStatus = isAwaiting ? Badge(badges, i.Sku) : null
            }).ToList()
        };
    }

    // Look up the given SKUs in both reference lists in one query. Returns SKU (upper) → status.
    private async Task<Dictionary<string, string>> BadgeAsync(
        IEnumerable<string?> skus, CancellationToken ct)
    {
        var keys = skus
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim().ToUpperInvariant())
            .Distinct()
            .ToList();

        if (keys.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var hits = await _db.InventoryItems
            .Where(i => keys.Contains(i.Sku))
            .Select(i => new { i.Kind, i.Sku })
            .ToListAsync(ct);

        var inStock = hits.Where(h => h.Kind == InventoryKind.InStock)
            .Select(h => h.Sku).ToHashSet(StringComparer.Ordinal);
        var onOrder = hits.Where(h => h.Kind == InventoryKind.PurchaseOrder)
            .Select(h => h.Sku).ToHashSet(StringComparer.Ordinal);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            map[key] = inStock.Contains(key) ? InventoryLineStatus.InStock
                : onOrder.Contains(key) ? InventoryLineStatus.PreOrdered
                : InventoryLineStatus.Unknown;
        }

        return map;
    }

    private static string Badge(Dictionary<string, string> badges, string? sku)
    {
        var key = sku?.Trim().ToUpperInvariant();
        return key is not null && badges.TryGetValue(key, out var status)
            ? status
            : InventoryLineStatus.Unknown;
    }
}
