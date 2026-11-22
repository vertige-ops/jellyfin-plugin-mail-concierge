using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.MailConcierge.Email;

/// <summary>
/// The two daily e-mail limits, over a rolling 24 hours, kept in memory only: one for each recipient and one for the whole server.
/// A recipient is known only by an HMAC-SHA-256 of the lower-cased address, keyed with a random value made at start-up and never stored,
/// so no address is ever kept and a hash cannot be matched against a list of known addresses. The memory is bounded by the
/// global limit, because every e-mail that is counted is kept for 24 hours and no more than the global limit is ever counted.
/// </summary>
public sealed class EmailQuota
{
    /// <summary>The length of the rolling window, in hours.</summary>
    public const int WindowHours = 24;

    /// <summary>The default number of e-mails per recipient per day.</summary>
    public const int DefaultPerRecipient = 10;

    /// <summary>The default number of e-mails per day for the whole server.</summary>
    public const int DefaultPerDay = 200;

    /// <summary>The highest value accepted for the per-recipient limit.</summary>
    public const int MaxPerRecipient = 1000;

    /// <summary>The highest value accepted for the global limit.</summary>
    public const int MaxPerDay = 10000;

    private static readonly TimeSpan Window = TimeSpan.FromHours(WindowHours);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly object _lock = new();
    private readonly TimeProvider _time;
    private readonly Queue<DateTimeOffset> _all = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _byRecipient = new(StringComparer.Ordinal);
    private DateTimeOffset _lastSweep;
    private DateTimeOffset? _reachedAt;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmailQuota"/> class.
    /// </summary>
    /// <param name="time">The time source.</param>
    public EmailQuota(TimeProvider time)
    {
        _time = time;
        _lastSweep = time.GetUtcNow();
    }

    /// <summary>Which limit stopped a message.</summary>
    public enum Refusal
    {
        /// <summary>Nothing: the message is counted and may be sent.</summary>
        None,

        /// <summary>The recipient already received its share of the last 24 hours.</summary>
        Recipient,

        /// <summary>The server already sent its share of the last 24 hours.</summary>
        Global,
    }

    /// <summary>
    /// Counts a message for a recipient when both limits allow it. A refused message is not counted.
    /// </summary>
    /// <param name="address">The recipient address; only a hash of it is kept.</param>
    /// <param name="perRecipientLimit">The e-mails allowed per recipient in 24 hours.</param>
    /// <param name="globalLimit">The e-mails allowed in 24 hours for the whole server.</param>
    /// <returns><see cref="Refusal.None"/> when the message is counted, otherwise the limit that stopped it.</returns>
    public Refusal TryAcquire(string address, int perRecipientLimit, int globalLimit)
    {
        var key = HashOf(address);
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            Prune(now);
            _byRecipient.TryGetValue(key, out var hits);
            while (hits is { Count: > 0 } && hits.Peek() <= now - Window)
            {
                hits.Dequeue();
            }

            if ((hits?.Count ?? 0) >= perRecipientLimit)
            {
                _reachedAt = now;
                return Refusal.Recipient;
            }

            if (_all.Count >= globalLimit)
            {
                _reachedAt = now;
                return Refusal.Global;
            }

            if (hits is null)
            {
                hits = new Queue<DateTimeOffset>();
                _byRecipient[key] = hits;
            }

            hits.Enqueue(now);
            _all.Enqueue(now);
            return Refusal.None;
        }
    }

    /// <summary>
    /// Reads the state of the last 24 hours without changing it.
    /// </summary>
    /// <param name="perRecipientLimit">The e-mails allowed per recipient in 24 hours.</param>
    /// <returns>The number counted, when a limit last stopped a message, and how many recipients are at their limit now.</returns>
    public (int Used, DateTimeOffset? LimitReachedAt, int RecipientsBlocked) Snapshot(int perRecipientLimit)
    {
        lock (_lock)
        {
            var from = _time.GetUtcNow() - Window;
            var used = 0;
            foreach (var t in _all)
            {
                if (t > from)
                {
                    used++;
                }
            }

            var blocked = 0;
            foreach (var hits in _byRecipient.Values)
            {
                var n = 0;
                foreach (var t in hits)
                {
                    if (t > from)
                    {
                        n++;
                    }
                }

                if (n >= perRecipientLimit)
                {
                    blocked++;
                }
            }

            return (used, _reachedAt is { } r && r > from ? r : null, blocked);
        }
    }

    /// <summary>
    /// Gives the limit to apply for a configured value: the default when it is not a positive number (a configuration edited by hand), at most the highest accepted value.
    /// </summary>
    /// <param name="configured">The configured value.</param>
    /// <param name="max">The highest accepted value.</param>
    /// <param name="fallback">The default.</param>
    /// <returns>The limit.</returns>
    internal static int Effective(int configured, int max, int fallback)
        => configured < 1 ? fallback : Math.Min(configured, max);

    private string HashOf(string address)
        => Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes((address ?? string.Empty).Trim().ToLower(CultureInfo.InvariantCulture))));

    private void Prune(DateTimeOffset now)
    {
        var from = now - Window;
        while (_all.Count > 0 && _all.Peek() <= from)
        {
            _all.Dequeue();
        }

        if (now - _lastSweep < SweepInterval)
        {
            return;
        }

        _lastSweep = now;
        List<string>? dead = null;
        foreach (var (key, hits) in _byRecipient)
        {
            while (hits.Count > 0 && hits.Peek() <= from)
            {
                hits.Dequeue();
            }

            if (hits.Count == 0)
            {
                (dead ??= new()).Add(key);
            }
        }

        if (dead is not null)
        {
            foreach (var key in dead)
            {
                _byRecipient.Remove(key);
            }
        }
    }
}
