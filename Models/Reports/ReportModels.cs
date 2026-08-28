namespace DigitalBoxApi.Models.Reports;

public class ShippableItemsRowModel
{
    public string Title { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public int OrderedQty { get; set; }
    public int OnHandQty { get; set; }
    public int ShippableQty { get; set; }
}

public class ShippableItemsResponseModel
{
    public List<ShippableItemsRowModel> Rows { get; set; } = new();
    public DateTime GeneratedAt { get; set; }
    public int OpenOrderCount { get; set; }
    public int CsvRowCount { get; set; }
    public int MatchedRowCount { get; set; }
}
