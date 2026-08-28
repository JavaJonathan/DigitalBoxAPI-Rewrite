namespace DigitalBoxApi.Entities;

public class Order
{
    public Guid Id { get; set; }

    /// <summary>Marketplace order number parsed from the slip. May be empty when parsing failed.</summary>
    public string OrderNumber { get; set; } = string.Empty;

    public Marketplace Marketplace { get; set; } = Marketplace.Unknown;

    public DateOnly? ShipDate { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.Open;

    public ParseStatus ParseStatus { get; set; } = ParseStatus.Parsed;

    /// <summary>
    /// Lower-cased, alphanumerics-only concatenation of every line-item title plus the order
    /// number(s). Backs trigram search — mirrors the old HttpHelper.filterForSearchValue behaviour.
    /// </summary>
    public string SearchText { get; set; } = string.Empty;

    /// <summary>Free-text operator name captured when the order was shipped or cancelled.</summary>
    public string? ActionedBy { get; set; }

    public Guid PackingSlipId { get; set; }
    public PackingSlip PackingSlip { get; set; } = null!;

    public List<OrderLineItem> LineItems { get; set; } = new();
    public List<OrderEvent> Events { get; set; } = new();

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ShippedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
}
