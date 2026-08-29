using System.Collections.Concurrent;

namespace DigitalBoxApi.Services;

// In-memory failed-login lockout. Keyed by client IP so one bad actor cannot lock the whole
// warehouse out. Not distributed — fine for a single instance. The threshold is deliberately
// loose (real staff fat-finger a username-only login often); it exists to stop scripted
// brute force, and password length is the real defence.
public class LoginThrottle
{
    private const int MaxFailures = 50;
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(15);

    private sealed class Entry
    {
        public int Failures;
        public DateTime FirstFailureUtc;
        public DateTime? LockedUntilUtc;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public bool IsLockedOut(string key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (entry.LockedUntilUtc is { } until)
        {
            if (DateTime.UtcNow < until)
            {
                return true;
            }

            _entries.TryRemove(key, out _);
        }

        return false;
    }

    public void RecordFailure(string key)
    {
        var entry = _entries.GetOrAdd(key, _ => new Entry { FirstFailureUtc = DateTime.UtcNow });

        lock (entry)
        {
            if (DateTime.UtcNow - entry.FirstFailureUtc > LockoutWindow)
            {
                entry.Failures = 0;
                entry.FirstFailureUtc = DateTime.UtcNow;
                entry.LockedUntilUtc = null;
            }

            entry.Failures++;
            if (entry.Failures >= MaxFailures)
            {
                entry.LockedUntilUtc = DateTime.UtcNow.Add(LockoutWindow);
            }
        }
    }

    public void RecordSuccess(string key) => _entries.TryRemove(key, out _);
}
