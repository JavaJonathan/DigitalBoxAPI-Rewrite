namespace DigitalBoxApi.Entities;

// One row of an uploaded reference list. The whole set for a Kind is deleted and re-inserted
// on every upload, so there is no per-row timestamp; InventoryUpload carries the metadata.
public class InventoryItem
{
    public Guid Id { get; set; }

    public InventoryKind Kind { get; set; }

    // Upper-cased, trimmed SKU: the match key. Indexed with Kind.
    public string Sku { get; set; } = string.Empty;

    // The SKU as it appeared in the file, for display.
    public string SkuRaw { get; set; } = string.Empty;

    // Product title from the file. Empty for purchase-order lists that only carry SKUs.
    public string Title { get; set; } = string.Empty;

    // On-hand quantity. 0 for purchase-order lists (they only signal "on order").
    public int OnHand { get; set; }
}
