using System.Text.RegularExpressions;

namespace DigitalBoxApi.Services;

/// <summary>One row of the uploaded inventory CSV, already column-mapped and normalized.</summary>
public readonly record struct InventoryRow(string Sku, string Title, int OnHand);

/// <summary>An open-order line item, flattened for matching. Carries its order id.</summary>
public readonly record struct OpenOrderLine(Guid OrderId, string? Sku, string Title, int Quantity);

/// <summary>Order-level identity + queue-sort keys, so the allocation pass can pick in queue order.</summary>
public readonly record struct OpenOrderInfo(
    Guid OrderId,
    string OrderNumber,
    string Marketplace,
    bool IsPriority,
    DateOnly? ShipDate,
    DateTime CreatedAt);

public sealed record ShippableItem(
    string Title,
    string Sku,
    int OrderedQty,
    int OnHandQty,
    int ShippableQty,
    int ShortQty,
    string Coverage);

/// <summary>Open-order demand that matched no (non-variant) inventory row.</summary>
public sealed record UnmatchedDemand(string? Sku, string Title, int OrderedQty, int OrderCount);

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

public sealed record ShippableItemsResult(
    IReadOnlyList<ShippableItem> Items,
    IReadOnlyList<UnmatchedDemand> UnmatchedDemand,
    IReadOnlyList<ShippableOrder> Orders,
    int OrdersShippable,
    int OrdersPartial,
    int OrdersBlocked,
    int OrdersNeedsCheck,
    int UnitsShippable);

/// <summary>
/// Cross-references an inventory list against open-order demand. Successor to the old
/// InventoryCheckWorker.js. Pure — no DB / IO / DI.
///
/// Two products:
///   * item-level rows  — per matched SKU: ordered vs on-hand, what's shippable, what's short.
///   * order-level rows  — walking open orders in queue order (priority, then oldest),
///     decrementing a working copy of stock, so each order reads as Shippable / Partial /
///     Blocked / NeedsCheck given real contention for scarce SKUs.
///
/// Matching rules (fix the old worker's double-count + fragile substring bugs):
///   * a line with its own SKU matches ONLY an exact (case-insensitive) inventory SKU — a
///     mismatch there is a real gap, not a formatting quirk, so it is reported, not papered
///     over with a title guess;
///   * a line with no SKU falls back to "an inventory SKU (len >= 4) appears in the title",
///     longest SKU wins;
///   * every line is attributed to AT MOST ONE inventory SKU, so demand is never summed twice;
///   * inventory SKUs ending "-&lt;digit&gt;" (variant children) are excluded from matching —
///     any order demand for them surfaces under UnmatchedDemand instead of vanishing.
/// </summary>
public static partial class ShippableItemsReport
{
    [GeneratedRegex(@"-\d$")]
    private static partial Regex VariantSuffix();

    /// <summary>
    /// Shortest inventory SKU allowed as a bare title substring. A 2–3 char code matches far
    /// too much text; real marketplace exports never use SKUs that short.
    /// </summary>
    private const int MinTitleMatchSkuLength = 4;

    private sealed class InvAgg
    {
        public string DisplaySku = string.Empty;
        public string Title = string.Empty;
        public int OnHand;
        public int OrderedQty;
    }

    private sealed class UnmatchedAgg
    {
        public string? Sku;
        public string Title = string.Empty;
        public int OrderedQty;
        public readonly HashSet<Guid> OrderIds = new();
    }

    public static ShippableItemsResult Build(
        IReadOnlyCollection<InventoryRow> inventory,
        IReadOnlyCollection<OpenOrderInfo> openOrders,
        IReadOnlyCollection<OpenOrderLine> openLines)
    {
        // 1. Normalize inventory into a SKU-keyed aggregate (upper-cased key; dupes sum on-hand).
        var stock = new Dictionary<string, InvAgg>(StringComparer.Ordinal);
        foreach (var inv in inventory)
        {
            var raw = (inv.Sku ?? string.Empty).Trim();
            var key = raw.ToUpperInvariant();

            // Blank SKU is unmatchable; variant children ("ABC-1") are excluded by convention.
            if (key.Length == 0 || VariantSuffix().IsMatch(key))
            {
                continue;
            }

            var onHand = Math.Max(0, inv.OnHand);
            if (stock.TryGetValue(key, out var agg))
            {
                agg.OnHand += onHand;
                if (string.IsNullOrEmpty(agg.Title))
                {
                    agg.Title = inv.Title?.Trim() ?? string.Empty;
                }
            }
            else
            {
                stock[key] = new InvAgg
                {
                    DisplaySku = raw,
                    Title = inv.Title?.Trim() ?? string.Empty,
                    OnHand = onHand,
                };
            }
        }

        // 2. Title-fallback index: matchable keys only, longest first (most specific wins).
        var titleIndex = stock.Keys
            .Where(k => k.Length >= MinTitleMatchSkuLength)
            .OrderByDescending(k => k.Length)
            .ToArray();

        string? MatchKey(string? sku, string? title)
        {
            var s = sku?.Trim();
            if (!string.IsNullOrEmpty(s))
            {
                var up = s.ToUpperInvariant();
                return stock.ContainsKey(up) ? up : null;
            }

            if (string.IsNullOrEmpty(title))
            {
                return null;
            }

            foreach (var k in titleIndex)
            {
                if (title.Contains(k, StringComparison.OrdinalIgnoreCase))
                {
                    return k;
                }
            }

            return null;
        }

        // 3. Attribute each open line to one inventory key (or to the unmatched bucket).
        var unmatched = new Dictionary<string, UnmatchedAgg>(StringComparer.OrdinalIgnoreCase);
        var matchedLines = new List<(OpenOrderLine Line, string? Key)>(openLines.Count);

        foreach (var li in openLines)
        {
            var key = MatchKey(li.Sku, li.Title);
            matchedLines.Add((li, key));

            if (key is not null)
            {
                stock[key].OrderedQty += li.Quantity;
                continue;
            }

            var skuTrim = li.Sku?.Trim();
            var hasSku = !string.IsNullOrEmpty(skuTrim);
            var bucketKey = hasSku ? skuTrim! : "title:" + (li.Title?.Trim().ToLowerInvariant() ?? string.Empty);
            if (!unmatched.TryGetValue(bucketKey, out var ua))
            {
                ua = new UnmatchedAgg { Sku = hasSku ? skuTrim : null, Title = li.Title?.Trim() ?? string.Empty };
                unmatched[bucketKey] = ua;
            }

            ua.OrderedQty += li.Quantity;
            ua.OrderIds.Add(li.OrderId);
        }

        // 4. Item-level rows.
        var items = new List<ShippableItem>();
        foreach (var agg in stock.Values)
        {
            if (agg.OrderedQty <= 0)
            {
                continue;
            }

            var shippable = Math.Min(agg.OrderedQty, agg.OnHand);
            var shortQty = Math.Max(0, agg.OrderedQty - agg.OnHand);
            var coverage = agg.OnHand >= agg.OrderedQty ? "Covered"
                : agg.OnHand > 0 ? "Partial"
                : "Blocked";

            items.Add(new ShippableItem(
                agg.Title, agg.DisplaySku, agg.OrderedQty, agg.OnHand, shippable, shortQty, coverage));
        }

        items = items
            .OrderByDescending(i => i.ShortQty)
            .ThenByDescending(i => i.ShippableQty)
            .ThenBy(i => i.Sku, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 5. Order-level allocation — pick in the same order the Open queue presents.
        var working = stock.ToDictionary(kv => kv.Key, kv => kv.Value.OnHand, StringComparer.Ordinal);
        var linesByOrder = matchedLines
            .GroupBy(m => m.Line.OrderId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var orderedOrders = openOrders
            .OrderByDescending(o => o.IsPriority)
            .ThenBy(o => o.ShipDate ?? DateOnly.MaxValue)
            .ThenBy(o => o.CreatedAt)
            .ThenBy(o => o.OrderId);

        var orders = new List<ShippableOrder>();
        int nShip = 0, nPart = 0, nBlock = 0, nCheck = 0;

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
                status = "Blocked";
            }
            else
            {
                status = "Partial";
            }

            switch (status)
            {
                case "Shippable": nShip++; break;
                case "Partial": nPart++; break;
                case "Blocked": nBlock++; break;
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
            "Blocked" => 2,
            _ => 3,
        };
        orders = orders.OrderBy(o => StatusRank(o.Status)).ToList();

        // 6. Unmatched demand + summary.
        var unmatchedOut = unmatched.Values
            .Select(u => new UnmatchedDemand(u.Sku, u.Title, u.OrderedQty, u.OrderIds.Count))
            .OrderByDescending(u => u.OrderedQty)
            .ThenBy(u => u.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ShippableItemsResult(
            items,
            unmatchedOut,
            orders,
            nShip,
            nPart,
            nBlock,
            nCheck,
            items.Sum(i => i.ShippableQty));
    }
}
