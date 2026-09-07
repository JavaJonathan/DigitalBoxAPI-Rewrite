namespace DigitalBoxApi.Entities;

public enum Marketplace
{
    Unknown = 0,
    Amazon,
    Ebay,
    Walmart,
    Shopify
}

public enum OrderStatus
{
    Open = 0,
    Shipped,
    Cancelled
}

// How confident we are in the data extracted from the packing-slip PDF.
public enum ParseStatus
{
    // Fields extracted cleanly.
    Parsed = 0,

    // Parsed with gaps or low confidence — a human should check it.
    NeedsReview,

    // The PDF could not be parsed at all; the order is a stub.
    Failed
}

public enum UserRole
{
    // Warehouse staff: full order access, no user administration.
    User = 0,

    // Can manage user accounts and reset passwords. Seeded via CLI only.
    Admin
}

public enum OrderEventType
{
    Created = 0,
    Shipped,
    Cancelled,
    Edited,

    // A shipped or cancelled order was undone back to Open.
    Reopened
}

// The two reference lists the admin order-lookup screen cross-references line items against.
// One "current" snapshot of each is held server-side and replaced on re-upload.
public enum InventoryKind
{
    // SKUs physically on hand.
    InStock = 0,

    // SKUs on an open purchase order — incoming, not yet in stock.
    PurchaseOrder
}
