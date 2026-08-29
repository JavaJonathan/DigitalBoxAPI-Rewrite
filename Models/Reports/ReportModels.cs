namespace DigitalBoxApi.Models.Reports;

public class ShippableItemsRowModel
{
    public string Title { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public int OrderedQty { get; set; }
    public int OnHandQty { get; set; }
    public int ShippableQty { get; set; }
    public int ShortQty { get; set; }

    /// <summary>"Covered" | "Partial" | "Blocked".</summary>
    public string Coverage { get; set; } = string.Empty;
}

/// <summary>Open-order demand with no matching (non-variant) row in the uploaded inventory.</summary>
public class UnmatchedDemandRowModel
{
    public string? Sku { get; set; }
    public string Title { get; set; } = string.Empty;
    public int OrderedQty { get; set; }
    public int OrderCount { get; set; }
}

public class ShippableOrderShortLineModel
{
    public string Title { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public int OrderedQty { get; set; }
    public int AvailableQty { get; set; }
}

public class ShippableOrderRowModel
{
    public Guid OrderId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string Marketplace { get; set; } = string.Empty;
    public bool IsPriority { get; set; }
    public int LineCount { get; set; }
    public int CoveredLineCount { get; set; }

    /// <summary>"Shippable" | "Partial" | "Blocked" | "NeedsCheck".</summary>
    public string Status { get; set; } = string.Empty;
    public List<ShippableOrderShortLineModel> ShortLines { get; set; } = new();
}

public class ShippableItemsResponseModel
{
    public List<ShippableItemsRowModel> Rows { get; set; } = new();
    public List<UnmatchedDemandRowModel> UnmatchedDemand { get; set; } = new();
    public List<ShippableOrderRowModel> Orders { get; set; } = new();

    public DateTime GeneratedAt { get; set; }
    public int OpenOrderCount { get; set; }
    public int CsvRowCount { get; set; }
    public int MatchedRowCount { get; set; }

    public int OrdersShippable { get; set; }
    public int OrdersPartial { get; set; }
    public int OrdersBlocked { get; set; }
    public int OrdersNeedsCheck { get; set; }
    public int UnitsShippable { get; set; }
}
