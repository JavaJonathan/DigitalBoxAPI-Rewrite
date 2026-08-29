namespace DigitalBoxApi.Entities;

// Append-only audit trail for an order. Powers the history views and future undo.
public class OrderEvent
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;

    public OrderEventType Type { get; set; }

    // Operator display-name snapshot for Shipped/Cancelled events; null for system events.
    public string? Actor { get; set; }

    // Stable reference to that user. Soft (no FK) — null for system / pre-accounts events.
    public Guid? ActorUserId { get; set; }

    // Optional human-readable detail (e.g. what an Edited event changed).
    public string? Detail { get; set; }

    public DateTime OccurredAt { get; set; }
}
