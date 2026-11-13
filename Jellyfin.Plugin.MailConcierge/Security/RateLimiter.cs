using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MailConcierge.Security;

/// <summary>
/// In-memory sliding window rate limiter with temporary blocks. Expired entries are pruned
/// so that memory stays bounded under a flood of distinct keys.
/// </summary>
public sealed class RateLimiter
{
    /// <summary>The most entries kept at once; beyond it the oldest are evicted.</summary>
    internal const int DefaultMaxEntries = 50_000;

    private const int SweepThreshold = 1024;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Dictionary<(string Bucket, string Key), Entry> _entries = new();
    private readonly int _maxEntries;
    private DateTimeOffset _lastSweep;
    private int _nextSweepAt = SweepThreshold;
    private long _sequence;

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimiter"/> class.
    /// </summary>
    /// <param name="time">The time source.</param>
    public RateLimiter(TimeProvider time)
        : this(time, DefaultMaxEntries)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimiter"/> class with a given entry cap.
    /// </summary>
    /// <param name="time">The time source.</param>
    /// <param name="maxEntries">The most entries kept at once.</param>
    internal RateLimiter(TimeProvider time, int maxEntries)
    {
        _time = time;
        _maxEntries = Math.Max(10, maxEntries);
        _lastSweep = time.GetUtcNow();
    }

    /// <summary>
    /// Gets the number of tracked entries (for tests).
    /// </summary>
    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Gets the number of sweeps performed (for tests).
    /// </summary>
    internal int SweepCount { get; private set; }

    /// <summary>
    /// Records an attempt if fewer than <paramref name="limit"/> happened within <paramref name="window"/>.
    /// </summary>
    /// <param name="bucket">The bucket name.</param>
    /// <param name="key">The key inside the bucket.</param>
    /// <param name="limit">The maximum number of attempts in the window.</param>
    /// <param name="window">The window length.</param>
    /// <returns>True when the attempt is allowed; false when refused or blocked.</returns>
    public bool TryAcquire(string bucket, string key, int limit, TimeSpan window)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            Sweep(now);
            var id = (bucket, key);
            var entry = GetOrAdd(id, now);

            if (entry.BlockedUntil > now)
            {
                return false;
            }

            while (entry.Hits.Count > 0 && entry.Hits.Peek() <= now - window)
            {
                entry.Hits.Dequeue();
            }

            if (entry.Hits.Count >= limit)
            {
                entry.Window = window;
                return false;
            }

            entry.Hits.Enqueue(now);
            entry.Window = window;
            return true;
        }
    }

    /// <summary>
    /// Blocks a key for the given duration.
    /// </summary>
    /// <param name="bucket">The bucket name.</param>
    /// <param name="key">The key inside the bucket.</param>
    /// <param name="blockFor">The block duration.</param>
    public void Penalize(string bucket, string key, TimeSpan blockFor)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            Sweep(now);
            var id = (bucket, key);
            var entry = GetOrAdd(id, now);

            var until = now + blockFor;
            if (until > entry.BlockedUntil)
            {
                entry.BlockedUntil = until;
            }
        }
    }

    /// <summary>
    /// Tells whether a key is currently blocked.
    /// </summary>
    /// <param name="bucket">The bucket name.</param>
    /// <param name="key">The key inside the bucket.</param>
    /// <returns>True when the key is blocked.</returns>
    public bool IsBlocked(string bucket, string key)
    {
        lock (_lock)
        {
            return _entries.TryGetValue((bucket, key), out var entry) && entry.BlockedUntil > _time.GetUtcNow();
        }
    }

    private Entry GetOrAdd((string Bucket, string Key) id, DateTimeOffset now)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            return entry;
        }

        if (_entries.Count >= _maxEntries)
        {
            Sweep(now, force: true);
            if (_entries.Count >= _maxEntries)
            {
                // Still full of live entries: evict a tenth, so the cost is amortised. Entries keyed by client address go first, so
                // that a flood of distinct addresses cannot reset the server-wide and per-account budgets; then unblocked, then oldest.
                var batch = Math.Max(1, _maxEntries / 10);
                var victims = _entries
                    .OrderBy(kv => !RateLimitBuckets.IsPerClientAddress(kv.Key.Bucket))
                    .ThenBy(kv => kv.Value.BlockedUntil > now)
                    .ThenBy(kv => kv.Value.Order)
                    .Take(batch)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var victim in victims)
                {
                    _entries.Remove(victim);
                }
            }
        }

        entry = new Entry { Order = ++_sequence };
        _entries[id] = entry;
        return entry;
    }

    private void Sweep(DateTimeOffset now, bool force = false)
    {
        if (!force && _entries.Count < _nextSweepAt && now - _lastSweep < SweepInterval)
        {
            return;
        }

        _lastSweep = now;
        SweepCount++;
        List<(string, string)>? dead = null;
        foreach (var (id, entry) in _entries)
        {
            var blocked = entry.BlockedUntil > now;
            var live = entry.Hits.Count > 0 && entry.Hits.Peek() > now - entry.Window;
            if (!blocked && !live)
            {
                (dead ??= new()).Add(id);
            }
        }

        if (dead is not null)
        {
            foreach (var id in dead)
            {
                _entries.Remove(id);
            }
        }

        _nextSweepAt = Math.Max(SweepThreshold, 2 * _entries.Count);
    }

    private sealed class Entry
    {
        public long Order { get; init; }

        public Queue<DateTimeOffset> Hits { get; } = new();

        public TimeSpan Window { get; set; }

        public DateTimeOffset BlockedUntil { get; set; }
    }
}
