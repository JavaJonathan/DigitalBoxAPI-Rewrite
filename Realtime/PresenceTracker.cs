namespace DigitalBoxApi.Realtime;

// In-memory record of who is connected. One user can hold several connections (multiple tabs);
// they only count as offline once the last one drops. Registered as a singleton.
//
// Process-local. If the API is ever scaled past a single instance this needs a SignalR
// backplane (Redis) and a shared presence store — not a concern at the current crew size.
public interface IPresenceTracker
{
    // Register a connection. Returns true if this made the user newly online.
    bool Add(Guid userId, string displayName, string connectionId);

    // Drop a connection. Returns true if this made the user go offline.
    bool Remove(Guid userId, string connectionId);

    // Everyone online now, ordered by display name.
    IReadOnlyList<OnlineUser> Snapshot();
}

public sealed class PresenceTracker : IPresenceTracker
{
    private sealed class Entry
    {
        public required string DisplayName { get; set; }
        public HashSet<string> Connections { get; } = [];
    }

    private readonly Dictionary<Guid, Entry> _users = [];
    private readonly Lock _gate = new();

    public bool Add(Guid userId, string displayName, string connectionId)
    {
        lock (_gate)
        {
            if (_users.TryGetValue(userId, out var entry))
            {
                entry.DisplayName = displayName;
                entry.Connections.Add(connectionId);
                return false;
            }

            _users[userId] = new Entry { DisplayName = displayName, Connections = { connectionId } };
            return true;
        }
    }

    public bool Remove(Guid userId, string connectionId)
    {
        lock (_gate)
        {
            if (!_users.TryGetValue(userId, out var entry))
            {
                return false;
            }

            entry.Connections.Remove(connectionId);
            if (entry.Connections.Count > 0)
            {
                return false;
            }

            _users.Remove(userId);
            return true;
        }
    }

    public IReadOnlyList<OnlineUser> Snapshot()
    {
        lock (_gate)
        {
            return _users
                .Select(kvp => new OnlineUser(kvp.Key, kvp.Value.DisplayName))
                .OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
