namespace DigitalBoxApi.Services;

// Order-level identity + queue-sort keys, so the allocation pass can pick in queue order.
public readonly record struct OpenOrderInfo(
    Guid OrderId,
    string OrderNumber,
    string Marketplace,
    bool IsPriority,
    DateOnly? ShipDate,
    DateTime CreatedAt);

public sealed record ShippableOrderShortLine(string Title, string? Sku, int OrderedQty, int AvailableQty);

public sealed record ShippableOrder(
    Guid OrderId,
    string OrderNumber,
    string Marketplace,
    bool IsPriority,
    int LineCount,
    int CoveredLineCount,
    string Status,
    IReadOnlyList<ShippableOrderShortLine> ShortLines);

public sealed record ShippableOrdersResult(
    IReadOnlyList<ShippableItem> Items,
    IReadOnlyList<UnmatchedDemand> UnmatchedDemand,
    IReadOnlyList<ShippableOrder> Orders,
    int OrdersShippable,
    int OrdersPartial,
    int OrdersNeedsCheck,
    int UnitsShippable);

// Cross-references an inventory list against open-order demand and allocates it per order.
// Matching (SKU/title attribution, item-level rows) lives in InventoryMatching, shared with
// the item-only ShippableItemsReport. Successor to the old InventoryCheckWorker.js.
//
// Order-level rows: walk open orders in queue order (priority, then oldest), decrementing a
// working copy of stock, so each order reads as Shippable / Partial / NeedsCheck given real
// contention for scarce SKUs. An order where every matched line is completely out of stock
// ("Blocked") is dropped entirely rather than surfaced — the client found that status
// unhelpful noise.
public static class ShippableOrdersReport
{
    public static ShippableOrdersResult Build(
        IReadOnlyCollection<InventoryRow> inventory,
        IReadOnlyCollection<OpenOrderInfo> openOrders,
        IReadOnlyCollection<OpenOrderLine> openLines)
    {
        var match = InventoryMatching.Match(inventory, openLines);

        // Order-level allocation: pick in the same order the Open queue presents.
        var working = match.Stock.ToDictionary(kv => kv.Key, kv => kv.Value.OnHand, StringComparer.Ordinal);
        var linesByOrder = match.MatchedLines
            .GroupBy(m => m.Line.OrderId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var orderedOrders = openOrders
            .OrderByDescending(o => o.IsPriority)
            .ThenBy(o => o.ShipDate ?? DateOnly.MaxValue)
            .ThenBy(o => o.CreatedAt)
            .ThenBy(o => o.OrderId);

        var orders = new List<ShippableOrder>();
        int nShip = 0, nPart = 0, nCheck = 0;

        foreach (var o in orderedOrders)
        {
            var lines = linesByOrder.TryGetValue(o.OrderId, out var l)
                ? l
                : new List<(OpenOrderLine Line, string? Key)>();

            var covered = 0;
            var matchedCount = 0;
            var anyAllocated = false;
            var hasUnknown = false;
            var shortLines = new List<ShippableOrderShortLine>();

            foreach (var (line, key) in lines)
            {
                if (key is null)
                {
                    hasUnknown = true;
                    continue;
                }

                matchedCount++;
                var avail = working.TryGetValue(key, out var w) ? w : 0;
                var take = Math.Max(0, Math.Min(line.Quantity, avail));
                if (take > 0)
                {
                    working[key] = avail - take;
                    anyAllocated = true;
                }

                if (take >= line.Quantity)
                {
                    covered++;
                }
                else
                {
                    var displaySku = string.IsNullOrWhiteSpace(line.Sku) ? key : line.Sku!.Trim();
                    shortLines.Add(new ShippableOrderShortLine(line.Title, displaySku, line.Quantity, take));
                }
            }

            string status;
            if (matchedCount == 0)
            {
                status = "NeedsCheck";
            }
            else if (covered == matchedCount)
            {
                status = hasUnknown ? "NeedsCheck" : "Shippable";
            }
            else if (!anyAllocated)
            {
                // Every matched line is completely out of stock ("Blocked"). Drop the order
                // rather than surface that status — see the class doc comment.
                continue;
            }
            else
            {
                status = "Partial";
            }

            switch (status)
            {
                case "Shippable": nShip++; break;
                case "Partial": nPart++; break;
                default: nCheck++; break;
            }

            orders.Add(new ShippableOrder(
                o.OrderId, o.OrderNumber, o.Marketplace, o.IsPriority,
                lines.Count, covered, status, shortLines));
        }

        // Allocation ran in queue order (correctness); present the list shippable-first so staff
        // see what they can pack now without scrolling. Ties keep the queue order.
        static int StatusRank(string s) => s switch
        {
            "Shippable" => 0,
            "Partial" => 1,
            _ => 2,
        };
        orders = orders.OrderBy(o => StatusRank(o.Status)).ToList();

        return new ShippableOrdersResult(
            match.Items,
            match.UnmatchedDemand,
            orders,
            nShip,
            nPart,
            nCheck,
            match.Items.Sum(i => i.ShippableQty));
    }
}
