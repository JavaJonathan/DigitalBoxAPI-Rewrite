namespace DigitalBoxApi.Realtime;

/// <summary>One warehouse user currently holding at least one live hub connection.</summary>
public record OnlineUser(Guid UserId, string DisplayName);

/// <summary>
/// A coworker action worth a quick, self-dismissing popup on everyone else's screen.
/// <paramref name="Verb"/> is one of <c>shipped</c> / <c>cancelled</c> / <c>reopened</c> /
/// <c>uploaded</c>; <paramref name="Count"/> is the number of orders affected.
/// </summary>
public record ActivityEvent(
    Guid Id,
    Guid ActorUserId,
    string ActorName,
    string Verb,
    int Count,
    DateTime At);

/// <summary>The methods the server invokes on connected browsers (SignalR strongly-typed hub).</summary>
public interface IActivityClient
{
    /// <summary>The full roster of who is online right now.</summary>
    Task Presence(IReadOnlyList<OnlineUser> online);

    /// <summary>A coworker shipped / cancelled / reopened / uploaded something.</summary>
    Task Activity(ActivityEvent evt);

    /// <summary>The open-order queue changed; clients viewing it should re-fetch.</summary>
    Task QueueChanged();
}
