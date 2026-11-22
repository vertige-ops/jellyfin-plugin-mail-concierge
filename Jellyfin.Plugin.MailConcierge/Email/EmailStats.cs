using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MailConcierge.Email;

/// <summary>
/// Counts, in memory only, the e-mails the queue delivered or gave up on: totals for the invitations and one row per account.
/// Nothing is written to disk and nothing but counters, ids and dates is kept: never an address, a subject, a body or an error text.
/// </summary>
public sealed class EmailStats
{
    /// <summary>The most account rows kept at once; beyond it the least recently updated row is evicted.</summary>
    internal const int DefaultMaxAccounts = 5000;

    private const string InvitationTemplate = "invitation";

    private readonly object _lock = new();
    private readonly Dictionary<Guid, Row> _rows = new();
    private readonly TimeProvider _time;
    private readonly int _maxAccounts;
    private readonly DateTimeOffset _since;
    private long _sequence;
    private int _invitationsSent;
    private int _invitationsFailed;

    /// <summary>
    /// Initializes a new instance of the <see cref="EmailStats"/> class.
    /// </summary>
    /// <param name="time">The time source.</param>
    public EmailStats(TimeProvider time)
        : this(time, DefaultMaxAccounts)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EmailStats"/> class with a given account cap.
    /// </summary>
    /// <param name="time">The time source.</param>
    /// <param name="maxAccounts">The most account rows kept at once.</param>
    internal EmailStats(TimeProvider time, int maxAccounts)
    {
        _time = time;
        _maxAccounts = Math.Max(1, maxAccounts);
        _since = time.GetUtcNow();
    }

    /// <summary>
    /// Counts an e-mail the server accepted.
    /// </summary>
    /// <param name="template">The template name.</param>
    /// <param name="userId">The user the e-mail concerns, if any.</param>
    public void RecordSent(string template, Guid? userId) => Record(template, userId, sent: true);

    /// <summary>
    /// Counts an e-mail given up on after the last attempt.
    /// </summary>
    /// <param name="template">The template name.</param>
    /// <param name="userId">The user the e-mail concerns, if any.</param>
    public void RecordFailed(string template, Guid? userId) => Record(template, userId, sent: false);

    /// <summary>
    /// Takes a copy of the counters.
    /// </summary>
    /// <returns>The totals and the account rows, the most recent send first (never sent last, then by id).</returns>
    public EmailStatsSnapshot Snapshot()
    {
        lock (_lock)
        {
            var users = _rows
                .Select(kv => new AccountEmailStats(kv.Key, kv.Value.Sent, kv.Value.Failed, kv.Value.LastSentAt))
                .OrderByDescending(u => u.LastSentAt.HasValue)
                .ThenByDescending(u => u.LastSentAt)
                .ThenBy(u => u.UserId)
                .ToList();
            return new EmailStatsSnapshot(_since, new EmailTotals(_invitationsSent, _invitationsFailed), users);
        }
    }

    private void Record(string template, Guid? userId, bool sent)
    {
        lock (_lock)
        {
            if (userId is null || string.Equals(template, InvitationTemplate, StringComparison.Ordinal))
            {
                if (sent)
                {
                    _invitationsSent++;
                }
                else
                {
                    _invitationsFailed++;
                }

                return;
            }

            var id = userId.Value;
            if (!_rows.TryGetValue(id, out var row))
            {
                if (_rows.Count >= _maxAccounts)
                {
                    var oldest = _rows.MinBy(kv => kv.Value.Order).Key;
                    _rows.Remove(oldest);
                }

                row = new Row();
                _rows[id] = row;
            }

            row.Order = ++_sequence;
            if (sent)
            {
                row.Sent++;
                row.LastSentAt = _time.GetUtcNow();
            }
            else
            {
                row.Failed++;
            }
        }
    }

    private sealed class Row
    {
        public long Order { get; set; }

        public int Sent { get; set; }

        public int Failed { get; set; }

        public DateTimeOffset? LastSentAt { get; set; }
    }
}
