namespace DigitalBoxApi.Entities;

public class Order
{
    public Guid Id { get; set; }

    // Marketplace order number parsed from the slip. May be empty when parsing failed.
    public string OrderNumber { get; set; } = string.Empty;

    public Marketplace Marketplace { get; set; } = Marketplace.Unknown;

    public DateOnly? ShipDate { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.Open;

    public ParseStatus ParseStatus { get; set; } = ParseStatus.Parsed;

    // Urgent-triage flag. Priority orders float to the top of the Open queue.
    public bool IsPriority { get; set; }

    // Free-text operator note. Editable in any status; folded into SearchText.
    public string? Notes { get; set; }

    // Lower-cased, alphanumerics-only concatenation of every line-item title, the order
    // number(s), and the note. Backs trigram search — mirrors the old
    // HttpHelper.filterForSearchValue behaviour.
    public string SearchText { get; set; } = string.Empty;

    // Display-name snapshot of who shipped or cancelled the order — point-in-time, kept even if
    // the user is later renamed or removed.
    public string? ActionedBy { get; set; }

    // Stable reference to that user. Soft (no FK) — null for pre-accounts history.
    public Guid? ActionedByUserId { get; set; }

    public Guid PackingSlipId { get; set; }
    public PackingSlip PackingSlip { get; set; } = null!;

    public List<OrderLineItem> LineItems { get; set; } = new();
    public List<OrderEvent> Events { get; set; } = new();

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ShippedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
}
