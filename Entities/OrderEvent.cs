namespace DigitalBoxApi.Entities;

/// <summary>Append-only audit trail for an order. Powers the history views and future undo.</summary>
public class OrderEvent
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;

    public OrderEventType Type { get; set; }

    /// <summary>Operator display-name snapshot for Shipped/Cancelled events; null for system events.</summary>
    public string? Actor { get; set; }

    /// <summary>Stable reference to that user. Soft (no FK) — null for system / pre-accounts events.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>Optional human-readable detail (e.g. what an Edited event changed).</summary>
    public string? Detail { get; set; }

    public DateTime OccurredAt { get; set; }
}
