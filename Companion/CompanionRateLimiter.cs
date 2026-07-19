using System;
using System.Collections.Concurrent;

namespace DesktopPlus.Companion
{
    /// <summary>
    /// Per-remote-IP failed-auth throttling. After too many bad tokens an IP is locked out
    /// briefly, turning brute-forcing the token into a non-starter without affecting legit use.
    /// </summary>
    internal sealed class CompanionRateLimiter
    {
        private const int MaxFailures = 8;
        private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(1);

        private readonly ConcurrentDictionary<string, Entry> _state = new();

        public bool IsLockedOut(string key)
        {
            if (_state.TryGetValue(key, out var entry))
            {
                return entry.Count >= MaxFailures && DateTime.UtcNow < entry.Until;
            }

            return false;
        }

        public void RegisterFailure(string key)
        {
            _state.AddOrUpdate(
                key,
                _ => new Entry(1, DateTime.UtcNow.Add(LockoutWindow)),
                (_, current) => new Entry(current.Count + 1, DateTime.UtcNow.Add(LockoutWindow)));
        }

        public void Reset(string key) => _state.TryRemove(key, out _);

        private readonly record struct Entry(int Count, DateTime Until);
    }
}
