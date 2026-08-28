namespace DigitalBoxApi.Entities;

public class OrderLineItem
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;

    public string Title { get; set; } = string.Empty;

    public int Quantity { get; set; }

    /// <summary>SKU/UPC if one could be isolated. Often embedded in <see cref="Title"/> instead.</summary>
    public string? Sku { get; set; }

    public int SortOrder { get; set; }
}
