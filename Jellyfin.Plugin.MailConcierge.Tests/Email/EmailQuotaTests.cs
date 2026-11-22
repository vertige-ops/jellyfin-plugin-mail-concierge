using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Email;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Email;

[Collection(LogoSeamCollection.Name)]
public class EmailQuotaTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void PerRecipient_RefusesTheNextOne_AndLeavesOthersAlone()
    {
        var quota = new EmailQuota(_time);

        Assert.Equal(EmailQuota.Refusal.None, quota.TryAcquire("bob@example.com", 2, 100));
        Assert.Equal(EmailQuota.Refusal.None, quota.TryAcquire("bob@example.com", 2, 100));
        Assert.Equal(EmailQuota.Refusal.Recipient, quota.TryAcquire("bob@example.com", 2, 100));
        Assert.Equal(EmailQuota.Refusal.None, quota.TryAcquire("alice@example.com", 2, 100));
    }

    [Fact]
    public void PerRecipient_IgnoresCaseAndSurroundingSpaces()
    {
        var quota = new EmailQuota(_time);
        Assert.Equal(EmailQuota.Refusal.None, quota.TryAcquire("Bob@Example.com", 1, 100));

        Assert.Equal(EmailQuota.Refusal.Recipient, quota.TryAcquire(" bob@example.com ", 1, 100));
    }

    [Fact]
    public void Global_RefusesOnceTheServerLimitIsReached_ForAnyRecipient()
    {
        var quota = new EmailQuota(_time);
        Assert.Equal(EmailQuota.Refusal.None, quota.TryAcquire("a@example.com", 5, 2));
        Assert.Equal(EmailQuota.Refusal.None, quota.TryAcquire("b@example.com", 5, 2));

        Assert.Equal(EmailQuota.Refusal.Global, quota.TryAcquire("c@example.com", 5, 2));
    }

    [Fact]
    public void ARefusedMessage_IsNotCounted()
    {
        var quota = new EmailQuota(_time);
        quota.TryAcquire("a@example.com", 1, 10);
        quota.TryAcquire("a@example.com", 1, 10);
        quota.TryAcquire("a@example.com", 1, 10);

        Assert.Equal(1, quota.Snapshot(1).Used);
    }

    [Fact]
    public void TheWindowRolls_EachMessageLeavesAfterTwentyFourHours()
    {
        var quota = new EmailQuota(_time);
        quota.TryAcquire("bob@example.com", 2, 100);
        _time.Advance(TimeSpan.FromHours(10));
        quota.TryAcquire("bob@example.com", 2, 100);
        Assert.Equal(EmailQuota.Refusal.Recipient, quota.TryAcquire("bob@example.com", 2, 100));

        _time.Advance(TimeSpan.FromHours(14) - TimeSpan.FromSeconds(1));
        Assert.Equal(EmailQuota.Refusal.Recipient, quota.TryAcquire("bob@example.com", 2, 100));

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(EmailQuota.Refusal.None, quota.TryAcquire("bob@example.com", 2, 100));
        Assert.Equal(EmailQuota.Refusal.Recipient, quota.TryAcquire("bob@example.com", 2, 100));

        _time.Advance(TimeSpan.FromHours(10));
        Assert.Equal(EmailQuota.Refusal.None, quota.TryAcquire("bob@example.com", 2, 100));
    }

    [Fact]
    public void TheGlobalWindowRolls_Too()
    {
        var quota = new EmailQuota(_time);
        quota.TryAcquire("a@example.com", 5, 1);
        Assert.Equal(EmailQuota.Refusal.Global, quota.TryAcquire("b@example.com", 5, 1));

        _time.Advance(TimeSpan.FromHours(24));

        Assert.Equal(EmailQuota.Refusal.None, quota.TryAcquire("b@example.com", 5, 1));
    }

    [Fact]
    public void Snapshot_ReportsUsed_RecipientsBlocked_AndWhenALimitLastStoppedAMessage()
    {
        var quota = new EmailQuota(_time);
        Assert.Null(quota.Snapshot(1).LimitReachedAt);
        quota.TryAcquire("a@example.com", 1, 10);
        quota.TryAcquire("b@example.com", 1, 10);
        quota.TryAcquire("c@example.com", 2, 10);
        _time.Advance(TimeSpan.FromMinutes(5));
        quota.TryAcquire("a@example.com", 1, 10);

        var (used, reachedAt, blocked) = quota.Snapshot(1);

        Assert.Equal(3, used);
        Assert.Equal(_time.GetUtcNow(), reachedAt);
        Assert.Equal(3, blocked);

        _time.Advance(TimeSpan.FromHours(24));
        var later = quota.Snapshot(1);
        Assert.Equal(0, later.Used);
        Assert.Equal(0, later.RecipientsBlocked);
        Assert.Null(later.LimitReachedAt);
    }

    [Fact]
    public void ExpiredRecipients_AreForgotten_SoMemoryStaysBounded()
    {
        var quota = new EmailQuota(_time);
        for (var i = 0; i < 50; i++)
        {
            quota.TryAcquire($"user{i}@example.com", 5, 1000);
        }

        _time.Advance(TimeSpan.FromHours(25));
        quota.TryAcquire("late@example.com", 5, 1000);

        Assert.Equal(1, quota.Snapshot(5).Used);
        var recipients = typeof(EmailQuota).GetField("_byRecipient", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(quota)!;
        Assert.Single((System.Collections.IDictionary)recipients);
    }

    [Fact]
    public void NoPlainAddressIsKept()
    {
        var quota = new EmailQuota(_time);
        quota.TryAcquire("secret.person@example.com", 5, 100);

        var fields = typeof(EmailQuota).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var keys = fields.Select(f => f.GetValue(quota)).OfType<System.Collections.IDictionary>().SelectMany(d => d.Keys.Cast<string>()).ToList();
        Assert.Single(keys);
        Assert.DoesNotContain("secret", keys[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, keys[0].Length);
    }

    [Theory]
    [InlineData(0, 1000, 10, 10)]
    [InlineData(-5, 1000, 10, 10)]
    [InlineData(5, 1000, 10, 5)]
    [InlineData(5000, 1000, 10, 1000)]
    public void Effective_UsesTheDefaultForANonPositiveValue_AndCapsTheHighOnes(int configured, int max, int fallback, int expected)
        => Assert.Equal(expected, EmailQuota.Effective(configured, max, fallback));

    // The queue, with its worker, a manual clock and a transport that remembers what it sent.
    [Fact]
    public async Task Queue_ThePerRecipientLimit_RefusesAndLogsTheSameWarningAsBefore_WithoutCountingSentOrFailed()
    {
        var h = new Harness(_time, perRecipient: 2, perDay: 100);
        Assert.Equal(EnqueueResult.Queued, h.Queue.Enqueue(h.Msg("bob@example.com")));
        Assert.Equal(EnqueueResult.Queued, h.Queue.Enqueue(h.Msg("bob@example.com")));

        Assert.Equal(EnqueueResult.LimitReached, h.Queue.Enqueue(h.Msg("bob@example.com")));
        Assert.Equal(EnqueueResult.Queued, h.Queue.Enqueue(h.Msg("alice@example.com")));

        var warning = Assert.Single(h.Warnings);
        Assert.Contains("daily limit", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("bob@example.com", warning, StringComparison.Ordinal);
        await h.RunAsync(3);
        Assert.Equal(3, h.Stats.Snapshot().Users.Sum(u => u.Sent));
        Assert.Equal(0, h.Stats.Snapshot().Users.Sum(u => u.Failed));
    }

    [Fact]
    public void Queue_TheGlobalLimit_RefusesEveryRecipient()
    {
        var h = new Harness(_time, perRecipient: 10, perDay: 2);
        Assert.Equal(EnqueueResult.Queued, h.Queue.Enqueue(h.Msg("a@example.com")));
        Assert.Equal(EnqueueResult.Queued, h.Queue.Enqueue(h.Msg("b@example.com")));

        Assert.Equal(EnqueueResult.LimitReached, h.Queue.Enqueue(h.Msg("c@example.com")));
    }

    [Fact]
    public void Queue_TheRollingWindow_FreesTheLimitAfterADay()
    {
        var h = new Harness(_time, perRecipient: 1, perDay: 100);
        Assert.Equal(EnqueueResult.Queued, h.Queue.Enqueue(h.Msg("bob@example.com")));
        Assert.Equal(EnqueueResult.LimitReached, h.Queue.Enqueue(h.Msg("bob@example.com")));

        _time.Advance(TimeSpan.FromHours(24));

        Assert.Equal(EnqueueResult.Queued, h.Queue.Enqueue(h.Msg("bob@example.com")));
    }

    [Theory]
    [InlineData("email-changed")]
    [InlineData("email-change-cancelled")]
    public void Queue_TheBypassNotices_AreNotCounted_AndNeverRefused(string template)
    {
        var h = new Harness(_time, perRecipient: 1, perDay: 1);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(EnqueueResult.Queued, h.Queue.Enqueue(h.Msg("bob@example.com", template)));
        }

        Assert.Equal(0, h.Queue.Quota.Snapshot(1).Used);
        Assert.Equal(EnqueueResult.Queued, h.Queue.Enqueue(h.Msg("bob@example.com")));
    }

    [Fact]
    public async Task Queue_TheTestEmail_IsNotCounted()
    {
        var h = new Harness(_time, perRecipient: 1, perDay: 1);
        Assert.Null(await h.Queue.SendTestAsync("bob@example.com"));
        Assert.Null(await h.Queue.SendTestAsync("bob@example.com"));

        Assert.Equal(0, h.Queue.Quota.Snapshot(1).Used);
        Assert.Equal(EnqueueResult.Queued, h.Queue.Enqueue(h.Msg("bob@example.com")));
    }

    [Fact]
    public async Task Notice_IsSentOncePerDayPerKind_ToTheAdministrator_WithoutAnyRecipientAddress()
    {
        var h = new Harness(_time, perRecipient: 1, perDay: 100, admin: "admin@example.org");
        h.Queue.Enqueue(h.Msg("victim@example.com"));
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(EnqueueResult.LimitReached, h.Queue.Enqueue(h.Msg("victim@example.com")));
        }

        await h.RunAsync(2);

        var notice = Assert.Single(h.Sent, m => m.Template == EmailQueue.QuotaNoticeTemplate);
        Assert.Equal("admin@example.org", notice.To);
        Assert.Contains("1", notice.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("victim", notice.Text + notice.Html + notice.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, h.Warnings.Count);
    }

    [Fact]
    public async Task Notice_ComesAgainAfterADay_AndEachKindHasItsOwn()
    {
        var h = new Harness(_time, perRecipient: 1, perDay: 2, admin: "admin@example.org");
        h.Queue.Enqueue(h.Msg("a@example.com"));
        h.Queue.Enqueue(h.Msg("a@example.com"));
        h.Queue.Enqueue(h.Msg("b@example.com"));
        h.Queue.Enqueue(h.Msg("c@example.com"));

        _time.Advance(TimeSpan.FromHours(24));
        h.Queue.Enqueue(h.Msg("a@example.com"));
        h.Queue.Enqueue(h.Msg("a@example.com"));
        h.Queue.Enqueue(h.Msg("b@example.com"));
        h.Queue.Enqueue(h.Msg("c@example.com"));

        await h.RunAsync(2 + 2 + 2 + 2);

        Assert.Equal(4, h.Sent.Count(m => m.Template == EmailQueue.QuotaNoticeTemplate));
    }

    [Fact]
    public async Task Notice_NotSentWithoutAnAddress_OrWithAnInvalidOne()
    {
        foreach (var address in new[] { string.Empty, "not an address" })
        {
            var h = new Harness(_time, perRecipient: 1, perDay: 100, admin: address);
            h.Queue.Enqueue(h.Msg("bob@example.com"));
            Assert.Equal(EnqueueResult.LimitReached, h.Queue.Enqueue(h.Msg("bob@example.com")));

            await h.RunAsync(1);

            Assert.DoesNotContain(h.Sent, m => m.Template == EmailQueue.QuotaNoticeTemplate);
        }
    }

    [Fact]
    public async Task Notice_GetsThroughWhenTheGlobalLimitIsReached_AndIsNotCountedNorInTheStats()
    {
        var h = new Harness(_time, perRecipient: 10, perDay: 1, admin: "admin@example.org");
        h.Queue.Enqueue(h.Msg("a@example.com"));
        Assert.Equal(EnqueueResult.LimitReached, h.Queue.Enqueue(h.Msg("b@example.com")));

        await h.RunAsync(2);

        Assert.Single(h.Sent, m => m.Template == EmailQueue.QuotaNoticeTemplate);
        Assert.Equal(1, h.Queue.Quota.Snapshot(10).Used);
        var snapshot = h.Stats.Snapshot();
        Assert.Equal(new EmailTotals(0, 0), snapshot.Invitations);
        Assert.Equal(1, snapshot.Users.Sum(u => u.Sent));
    }

    [Fact]
    public async Task Notice_TakingTheLastPlaceInTheQueue_LeavesLaterMessagesToTheNormalFullQueueRefusal()
    {
        var h = new Harness(_time, perRecipient: 1, perDay: 100, admin: "admin@example.org", capacity: 2);
        Assert.Equal(EnqueueResult.Queued, h.Queue.Enqueue(h.Msg("bob@example.com")));

        Assert.Equal(EnqueueResult.LimitReached, h.Queue.Enqueue(h.Msg("bob@example.com")));
        Assert.Equal(EnqueueResult.QueueFull, h.Queue.Enqueue(h.Msg("bob@example.com")));

        await h.RunAsync(2);
        Assert.Single(h.Sent, m => m.Template == EmailQueue.QuotaNoticeTemplate);
    }

    [Theory]
    [InlineData("fr", "limite quotidienne")]
    [InlineData("en", "daily e-mail limit")]
    [InlineData("de", "Limit")]
    [InlineData("es", "límite diario")]
    public async Task Notice_IsInTheDefaultLanguage(string language, string fragment)
    {
        var h = new Harness(_time, perRecipient: 1, perDay: 100, admin: "admin@example.org", language: language);
        h.Queue.Enqueue(h.Msg("bob@example.com"));
        h.Queue.Enqueue(h.Msg("bob@example.com"));

        await h.RunAsync(2);

        var notice = Assert.Single(h.Sent, m => m.Template == EmailQueue.QuotaNoticeTemplate);
        Assert.Contains(fragment, notice.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", notice.Html + notice.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("{limit}", notice.Text, StringComparison.Ordinal);
    }

    private sealed class Harness
    {
        private readonly CapturingTransport _transport = new();
        private readonly TestLog _log = new();

        public Harness(FakeTimeProvider time, int perRecipient, int perDay, string admin = "", string language = "en", int capacity = 100)
        {
            var config = new PluginConfiguration
            {
                PublicUrl = "https://jf.example",
                SmtpHost = "smtp.example",
                FromAddress = "jf@example.com",
                MaxEmailsPerRecipientPerDay = perRecipient,
                MaxEmailsPerDay = perDay,
                AdminNotificationAddress = admin,
                DefaultLanguage = language,
            };
            Stats = new EmailStats(time);
            Queue = new EmailQueue(() => config, _transport, _log, time, new[] { TimeSpan.Zero }, capacity, Stats);
        }

        public EmailQueue Queue { get; }

        public EmailStats Stats { get; }

        public IReadOnlyList<string> Warnings => _log.Warnings;

        public IReadOnlyList<EmailMessage> Sent => _transport.Sent;

        public EmailMessage Msg(string to, string template = "recovery")
            => new(to, "s", "<p>b</p>", "b", template, template == "invitation" ? null : Guid.NewGuid());

        // Starts the worker, waits until it delivered the given number of messages, and stops it.
        public async Task RunAsync(int delivered)
        {
            await Queue.StartAsync(CancellationToken.None);
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (_transport.Sent.Count < delivered && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(10, TestContext.Current.CancellationToken);
                }

                Assert.True(_transport.Sent.Count >= delivered, $"expected {delivered} deliveries, saw {_transport.Sent.Count}");
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
            finally
            {
                await Queue.StopAsync(CancellationToken.None);
            }
        }
    }

    private sealed class CapturingTransport : IEmailTransport
    {
        private readonly object _lock = new();
        private readonly List<EmailMessage> _sent = new();

        public IReadOnlyList<EmailMessage> Sent
        {
            get
            {
                lock (_lock)
                {
                    return _sent.ToList();
                }
            }
        }

        public Task SendAsync(EmailMessage m, CancellationToken ct)
        {
            lock (_lock)
            {
                _sent.Add(m);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class TestLog : ILogger<EmailQueue>
    {
        private readonly object _lock = new();
        private readonly List<string> _warnings = new();

        public IReadOnlyList<string> Warnings
        {
            get
            {
                lock (_lock)
                {
                    return _warnings.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                lock (_lock)
                {
                    _warnings.Add(formatter(state, exception));
                }
            }
        }
    }
}
