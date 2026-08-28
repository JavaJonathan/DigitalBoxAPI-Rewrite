using System.Text.RegularExpressions;

namespace DigitalBoxApi.Services;

/// <summary>One row of the uploaded inventory CSV, already column-mapped and normalized.</summary>
public readonly record struct InventoryRow(string Sku, string Title, int OnHand);

/// <summary>An open-order line item, flattened for matching.</summary>
public readonly record struct OpenOrderLine(string? Sku, string Title, int Quantity);

public sealed record ShippableItem(
    string Title, string Sku, int OrderedQty, int OnHandQty, int ShippableQty);

/// <summary>
/// Cross-references an inventory list against open-order demand. Port of the old
/// InventoryCheckWorker.js, with the fixes noted inline. Pure — no DB / IO / DI.
/// </summary>
public static partial class ShippableItemsReport
{
    [GeneratedRegex(@"-\d$")]
    private static partial Regex VariantSuffix();

    public static List<ShippableItem> Build(
        IReadOnlyCollection<InventoryRow> inventory,
        IReadOnlyCollection<OpenOrderLine> openLines)
    {
        var lines = openLines as IReadOnlyList<OpenOrderLine> ?? openLines.ToList();
        var result = new List<ShippableItem>();

        foreach (var inv in inventory)
        {
            var sku = inv.Sku?.Trim() ?? string.Empty;

            // Blank SKU is unmatchable — and Title.Contains("") is true for every line, which
            // would sum the whole queue into one bogus row.
            if (sku.Length == 0)
            {
                continue;
            }

            // Skip variant-child SKUs (customer convention: "ABC-1", "ABC-2").
            if (VariantSuffix().IsMatch(sku))
            {
                continue;
            }

            var orderedQty = 0;
            foreach (var li in lines)
            {
                // New vs. the original: also match the parsed Sku field, and case-insensitively
                // (the old worker was case-sensitive, title-substring only).
                if (string.Equals(li.Sku, sku, StringComparison.OrdinalIgnoreCase)
                    || li.Title.Contains(sku, StringComparison.OrdinalIgnoreCase))
                {
                    orderedQty += li.Quantity;
                }
            }

            if (orderedQty <= 0)
            {
                continue;
            }

            var onHand = Math.Max(0, inv.OnHand);
            var shippableQty = Math.Clamp(Math.Min(orderedQty, onHand), 0, orderedQty);

            result.Add(new ShippableItem(
                inv.Title?.Trim() ?? string.Empty, sku, orderedQty, onHand, shippableQty));
        }

        return result;
    }
}
