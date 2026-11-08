using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Lateral.Cms.Auth;

/// <summary>
/// Slows down credential guessing: after too many failed sign-ins from one client address within a
/// minute, further attempts from it are refused (429) until the window passes. In-memory and per
/// instance, which is enough as a brake; a shared store would be needed behind several instances.
/// </summary>
public sealed class AuthFailureThrottle(IOptions<AuthOptions> options, TimeProvider time)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int SweepThreshold = 10_000;

    private sealed class Entry(DateTime start)
    {
        public DateTime Start = start;
        public int Failures;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>True if this client is currently over the limit. <paramref name="retryAfter"/> says for how long.</summary>
    public bool IsBlocked(string client, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        var max = options.Value.MaxFailuresPerMinute;
        if (max <= 0 || !_entries.TryGetValue(client, out var entry))
        {
            return false;
        }

        var now = time.GetUtcNow().UtcDateTime;
        lock (entry)
        {
            // An expired entry is left for RecordFailure to reset and the sweep to remove: removing it here could discard a
            // failure another request is recording on the same entry.
            if (now - entry.Start >= Window)
            {
                return false;
            }

            if (entry.Failures < max)
            {
                return false;
            }

            retryAfter = entry.Start + Window - now;
            return true;
        }
    }

    public void RecordFailure(string client)
    {
        if (options.Value.MaxFailuresPerMinute <= 0)
        {
            return;
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (_entries.Count > SweepThreshold)
        {
            foreach (var (key, value) in _entries)
            {
                if (now - value.Start >= Window)
                {
                    _entries.TryRemove(key, out _);
                }
            }
        }

        var entry = _entries.GetOrAdd(client, _ => new Entry(now));
        lock (entry)
        {
            if (now - entry.Start >= Window)
            {
                entry.Start = now;
                entry.Failures = 0;
            }

            entry.Failures++;
        }
    }
}
