using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DigitalBoxApi.Realtime;

// The single realtime hub, mapped at /hub/activity. Tracks presence on connect/disconnect
// and is the channel the API broadcasts activity + queue-changed events over (see
// OrdersController). [Authorize] plus the JWT bearer OnMessageReceived hook in Program.cs
// means a connection carries the same identity as an HTTP request, and the per-request
// OnTokenValidated check still drops deactivated users.
[Authorize]
public sealed class PresenceHub : Hub<IActivityClient>
{
    private readonly IPresenceTracker _tracker;

    public PresenceHub(IPresenceTracker tracker)
    {
        _tracker = tracker;
    }

    public override async Task OnConnectedAsync()
    {
        var (userId, displayName) = Identify();
        var becameOnline = _tracker.Add(userId, displayName, Context.ConnectionId);

        // The joiner always needs the current roster; the rest of the crew only cares when the
        // online set actually changed (opening a second tab shouldn't notify anyone).
        if (becameOnline)
        {
            await Clients.All.Presence(_tracker.Snapshot());
        }
        else
        {
            await Clients.Caller.Presence(_tracker.Snapshot());
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var (userId, _) = Identify();
        if (_tracker.Remove(userId, Context.ConnectionId))
        {
            await Clients.All.Presence(_tracker.Snapshot());
        }

        await base.OnDisconnectedAsync(exception);
    }

    private (Guid UserId, string DisplayName) Identify()
    {
        var user = Context.User;
        var name = user?.FindFirstValue(ClaimTypes.Name) ?? user?.Identity?.Name ?? "Unknown";
        Guid.TryParse(user?.FindFirstValue(ClaimTypes.NameIdentifier), out var id);
        return (id, name);
    }
}
