namespace DigitalBoxApi.Services;

public sealed record ShippableItemsResult(
    IReadOnlyList<ShippableItem> Items,
    IReadOnlyList<UnmatchedDemand> UnmatchedDemand,
    int UnitsShippable);

// The original, item-centric Shippable Items report: aggregate open-order demand vs. on-hand
// inventory per SKU, with no per-order breakdown at all. Restores the old DigitalBox's
// InventoryCheckWorker.js concept ("can I ship this item, in general") using the corrected
// matching engine in InventoryMatching rather than that worker's known bugs (quantity
// string-concatenation, demand double-counted via cross-title substring matches). See
// ShippableOrdersReport for the order-specific counterpart added in the rewrite; both share
// the same matching engine, so their numbers always agree.
public static class ShippableItemsReport
{
    public static ShippableItemsResult Build(
        IReadOnlyCollection<InventoryRow> inventory,
        IReadOnlyCollection<OpenOrderLine> openLines)
    {
        var match = InventoryMatching.Match(inventory, openLines);
        return new ShippableItemsResult(
            match.Items,
            match.UnmatchedDemand,
            match.Items.Sum(i => i.ShippableQty));
    }
}
