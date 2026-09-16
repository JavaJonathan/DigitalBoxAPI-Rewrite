using System.Text.RegularExpressions;

namespace DigitalBoxApi.Services;

// One row of the uploaded inventory CSV, already column-mapped and normalized.
public readonly record struct InventoryRow(string Sku, string Title, int OnHand);

// An open-order line item, flattened for matching. Carries its order id.
public readonly record struct OpenOrderLine(Guid OrderId, string? Sku, string Title, int Quantity);

public sealed class InventoryStock
{
    public string DisplaySku = string.Empty;
    public string Title = string.Empty;
    public int OnHand;
    public int OrderedQty;
}

public sealed record ShippableItem(
    string Title,
    string Sku,
    int OrderedQty,
    int OnHandQty,
    int ShippableQty,
    int ShortQty,
    string Coverage);

// Open-order demand that matched no (non-variant) inventory row.
public sealed record UnmatchedDemand(string? Sku, string Title, int OrderedQty, int OrderCount);

public sealed record MatchResult(
    IReadOnlyDictionary<string, InventoryStock> Stock,
    IReadOnlyList<(OpenOrderLine Line, string? Key)> MatchedLines,
    IReadOnlyList<ShippableItem> Items,
    IReadOnlyList<UnmatchedDemand> UnmatchedDemand);

// Shared matching engine behind both Shippable reports (ShippableItemsReport, item-only, and
// ShippableOrdersReport, which layers per-order allocation on top). Successor to the old
// InventoryCheckWorker.js. Pure: no DB / IO / DI.
//
// Matching rules (fix the old worker's double-count + fragile substring bugs):
//   * a line with its own SKU matches ONLY an exact (case-insensitive) inventory SKU, and a
//     mismatch there is a real gap, not a formatting quirk, so it is reported, not papered
//     over with a title guess;
//   * a line with no SKU falls back to "an inventory SKU (len >= 4) appears in the title",
//     longest SKU wins;
//   * every line is attributed to AT MOST ONE inventory SKU, so demand is never summed twice;
//   * inventory SKUs ending "-<digit>" (variant children) are excluded from matching, so
//     any order demand for them surfaces under UnmatchedDemand instead of vanishing.
public static partial class InventoryMatching
{
    [GeneratedRegex(@"-\d$")]
    private static partial Regex VariantSuffix();

    // Shortest inventory SKU allowed as a bare title substring. A 2–3 char code matches far
    // too much text; real marketplace exports never use SKUs that short.
    private const int MinTitleMatchSkuLength = 4;

    private sealed class UnmatchedAgg
    {
        public string? Sku;
        public string Title = string.Empty;
        public int OrderedQty;
        public readonly HashSet<Guid> OrderIds = new();
    }

    public static MatchResult Match(
        IReadOnlyCollection<InventoryRow> inventory,
        IReadOnlyCollection<OpenOrderLine> openLines)
    {
        // 1. Normalize inventory into a SKU-keyed aggregate (upper-cased key; dupes sum on-hand).
        var stock = new Dictionary<string, InventoryStock>(StringComparer.Ordinal);
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
                stock[key] = new InventoryStock
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

        // 4. Item-level rows. Fully out-of-stock items ("Blocked": zero on hand) are dropped —
        // the client found that status unhelpful noise, so only items with at least partial
        // coverage are reported.
        var items = new List<ShippableItem>();
        foreach (var agg in stock.Values)
        {
            if (agg.OrderedQty <= 0 || agg.OnHand <= 0)
            {
                continue;
            }

            var shippable = Math.Min(agg.OrderedQty, agg.OnHand);
            var shortQty = Math.Max(0, agg.OrderedQty - agg.OnHand);
            var coverage = agg.OnHand >= agg.OrderedQty ? "Covered" : "Partial";

            items.Add(new ShippableItem(
                agg.Title, agg.DisplaySku, agg.OrderedQty, agg.OnHand, shippable, shortQty, coverage));
        }

        items = items
            .OrderByDescending(i => i.ShortQty)
            .ThenByDescending(i => i.ShippableQty)
            .ThenBy(i => i.Sku, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unmatchedOut = unmatched.Values
            .Select(u => new UnmatchedDemand(u.Sku, u.Title, u.OrderedQty, u.OrderIds.Count))
            .OrderByDescending(u => u.OrderedQty)
            .ThenBy(u => u.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MatchResult(stock, matchedLines, items, unmatchedOut);
    }
}
