namespace DigitalBoxApi.Realtime;

// One warehouse user currently holding at least one live hub connection.
public record OnlineUser(Guid UserId, string DisplayName);

// A coworker action worth a quick, self-dismissing popup on everyone else's screen.
// Verb is one of shipped / cancelled / reopened / uploaded; Count is the number of orders
// affected.
public record ActivityEvent(
    Guid Id,
    Guid ActorUserId,
    string ActorName,
    string Verb,
    int Count,
    DateTime At);

// The methods the server invokes on connected browsers (SignalR strongly-typed hub).
public interface IActivityClient
{
    // The full roster of who is online right now.
    Task Presence(IReadOnlyList<OnlineUser> online);

    // A coworker shipped / cancelled / reopened / uploaded something.
    Task Activity(ActivityEvent evt);

    // The open-order queue changed; clients viewing it should re-fetch. Carries the acting
    // user's id (Guid.Empty when unknown) so the browser that initiated the change can skip
    // its own echo — it already refreshes explicitly right after the HTTP call returns.
    Task QueueChanged(Guid actorUserId);
}
