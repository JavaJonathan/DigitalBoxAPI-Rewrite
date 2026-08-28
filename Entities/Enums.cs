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

public enum OrderEventType
{
    Created = 0,
    Shipped,
    Cancelled,
    Edited
}
