using System.ComponentModel.DataAnnotations;

namespace DigitalBoxApi.Models.Lookup;

// --- GET /api/lookup/order response -----------------------------------------

// Per-SKU cross-reference against the uploaded reference lists.
// "InStock" | "PreOrdered" | "Unknown".
public static class InventoryLineStatus
{
    public const string InStock = "InStock";
    public const string PreOrdered = "PreOrdered";
    public const string Unknown = "Unknown";
}

public class LookupLineItemModel
{
    public string Title { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public string? Sku { get; set; }

    // Only set when the order is awaiting shipment (mirrors the old per-item Status column).
    public string? InventoryStatus { get; set; }
}

public class LookupDigitalBoxModel
{
    public Guid OrderId { get; set; }
    public string Status { get; set; } = string.Empty;          // Open | Shipped | Cancelled
    public string Marketplace { get; set; } = string.Empty;
    public DateOnly? ShipDate { get; set; }
    public bool IsPriority { get; set; }
    public string? Notes { get; set; }
    public string ParseStatus { get; set; } = string.Empty;

    // Human summary of where the order stands, e.g. "Shipped", "Unshipped - In Stock",
    // "Awaiting Shipment in Digital Box", "Cancelled". Ported from OrderSearch.jsx.
    public string Summary { get; set; } = string.Empty;

    public List<LookupLineItemModel> LineItems { get; set; } = new();

    // >1 when the same order number was ingested from more than one packing slip.
    public int DuplicateCount { get; set; }
}

public class LookupShipToModel
{
    public string? Name { get; set; }
    public string? Street1 { get; set; }
    public string? Street2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
}

public class LookupShipStationModel
{
    public string OrderStatus { get; set; } = string.Empty;
    public DateTime? OrderDate { get; set; }
    public string? TrackingNumber { get; set; }
    public string? Carrier { get; set; }
    public LookupShipToModel? ShipTo { get; set; }
    public List<LookupLineItemModel> Items { get; set; } = new();
}

public class LookupResultModel
{
    public bool Found { get; set; }

    // "DigitalBox" | "ShipStation" | null
    public string? Source { get; set; }

    public string OrderNumber { get; set; } = string.Empty;

    public LookupDigitalBoxModel? DigitalBox { get; set; }
    public LookupShipStationModel? ShipStation { get; set; }

    // False when no ShipStation credentials are configured; the UI explains the DB-only result.
    public bool ShipStationConfigured { get; set; }

    // Generic message when ShipStation is configured but the call failed (never the raw error).
    public string? ShipStationError { get; set; }
}

// --- inventory snapshot endpoints ------------------------------------------

public class InventorySnapshotModel
{
    public string FileName { get; set; } = string.Empty;
    public int RowCount { get; set; }
    public DateTime UploadedAt { get; set; }
    public string? UploadedBy { get; set; }
}

public class InventoryStatusModel
{
    public InventorySnapshotModel? InStock { get; set; }
    public InventorySnapshotModel? PurchaseOrders { get; set; }
}

public class LookupOrderQueryModel
{
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public string OrderNumber { get; set; } = string.Empty;
}
