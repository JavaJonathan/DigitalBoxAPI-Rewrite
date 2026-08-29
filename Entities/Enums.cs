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

/// <summary>How confident we are in the data extracted from the packing-slip PDF.</summary>
public enum ParseStatus
{
    /// <summary>Fields extracted cleanly.</summary>
    Parsed = 0,

    /// <summary>Parsed with gaps or low confidence — a human should check it.</summary>
    NeedsReview,

    /// <summary>The PDF could not be parsed at all; the order is a stub.</summary>
    Failed
}

public enum UserRole
{
    /// <summary>Warehouse staff: full order access, no user administration.</summary>
    User = 0,

    /// <summary>Can manage user accounts and reset passwords. Seeded via CLI only.</summary>
    Admin
}

public enum OrderEventType
{
    Created = 0,
    Shipped,
    Cancelled,
    Edited,

    /// <summary>A shipped or cancelled order was undone back to Open.</summary>
    Reopened
}
