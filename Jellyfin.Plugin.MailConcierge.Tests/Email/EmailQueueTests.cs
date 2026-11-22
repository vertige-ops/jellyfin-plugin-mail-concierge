using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Email;
using Jellyfin.Plugin.MailConcierge.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Email;

[Collection(LogoSeamCollection.Name)]
public class EmailQueueTests
{
    private const string Password = "s3cr3t-smtp-p@ss";

    private static readonly EmailMessage Message = new("bob@example.com", "Hi", "<p>Hi</p>", "Hi", "recovery", Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static PluginConfiguration Config(int max = 10) => new()
    {
        PublicUrl = "https://jf.example",
        SmtpHost = "smtp.example",
        FromAddress = "jf@example.com",
        SmtpPassword = Password,
        MaxEmailsPerRecipientPerDay = max,
        MaxEmailsPerDay = max,
    };

    private static EmailQueue Create(PluginConfiguration config, IEmailTransport transport, TestLogger logger, int capacity = 100)
        => new(() => config, transport, logger, TimeProvider.System, new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero }, capacity);

    private static EmailQueue CreateWithStats(PluginConfiguration config, IEmailTransport transport, TestLogger logger, EmailStats stats, int capacity = 100)
        => new(() => config, transport, logger, TimeProvider.System, new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero }, capacity, stats);

    [Fact]
    public async Task Stats_SuccessCountsAsSent()
    {
        var stats = new EmailStats(TimeProvider.System);
        var transport = new FakeTransport();
        var logger = new TestLogger();
        var queue = CreateWithStats(Config(), transport, logger, stats);
        await queue.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message));
            Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message with { Template = "invitation", UserId = null }));
            await logger.WaitForAsync("Sent invitation e-mail");
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }

        var snapshot = stats.Snapshot();
        var row = Assert.Single(snapshot.Users);
        Assert.Equal(Message.UserId, row.UserId);
        Assert.Equal(1, row.Sent);
        Assert.Equal(0, row.Failed);
        Assert.NotNull(row.LastSentAt);
        Assert.Equal(new EmailTotals(1, 0), snapshot.Invitations);
    }

    [Fact]
    public async Task Stats_ThreeFailingAttempts_CountOneFailed()
    {
        var stats = new EmailStats(TimeProvider.System);
        var transport = new FakeTransport { Throw = new InvalidOperationException("boom") };
        var logger = new TestLogger();
        var queue = CreateWithStats(Config(), transport, logger, stats);
        await queue.StartAsync(CancellationToken.None);
        try
        {
            queue.Enqueue(Message);
            await logger.WaitForAsync("after 3 attempts");
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }

        Assert.Equal(3, transport.Calls);
        Assert.Equal(new AccountEmailStats(Message.UserId!.Value, 0, 1, null), Assert.Single(stats.Snapshot().Users));
    }

    [Fact]
    public async Task Stats_ARetryThatSucceeds_CountsOneSentAndNoFailure()
    {
        var stats = new EmailStats(TimeProvider.System);
        var transport = new FakeTransport { FailFirst = 2 };
        var logger = new TestLogger();
        var queue = CreateWithStats(Config(), transport, logger, stats);
        await queue.StartAsync(CancellationToken.None);
        try
        {
            queue.Enqueue(Message);
            await logger.WaitForAsync("Sent recovery e-mail");
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }

        var row = Assert.Single(stats.Snapshot().Users);
        Assert.Equal(1, row.Sent);
        Assert.Equal(0, row.Failed);
    }

    [Fact]
    public async Task Stats_SendTest_CountsNothing()
    {
        var stats = new EmailStats(TimeProvider.System);
        var queue = CreateWithStats(Config(), new FakeTransport(), new TestLogger(), stats);

        Assert.Null(await queue.SendTestAsync("bob@example.com"));
        var failing = CreateWithStats(Config(), new FakeTransport { Throw = new InvalidOperationException("boom") }, new TestLogger(), stats);
        Assert.NotNull(await failing.SendTestAsync("bob@example.com"));

        var snapshot = stats.Snapshot();
        Assert.Empty(snapshot.Users);
        Assert.Equal(new EmailTotals(0, 0), snapshot.Invitations);
    }

    [Fact]
    public async Task Stats_MessagesRefusedAtEnqueue_CountNothing()
    {
        var stats = new EmailStats(TimeProvider.System);
        var notConfigured = Config();
        notConfigured.SmtpHost = string.Empty;
        Assert.Equal(EnqueueResult.NotConfigured, CreateWithStats(notConfigured, new FakeTransport(), new TestLogger(), stats).Enqueue(Message));

        var logger = new TestLogger();
        var transport = new FakeTransport();
        var queue = CreateWithStats(Config(1), transport, logger, stats, capacity: 1);
        await queue.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message));
            await logger.WaitForAsync("Sent recovery e-mail");
            Assert.Equal(EnqueueResult.LimitReached, queue.Enqueue(Message));
            Assert.Equal(EnqueueResult.LimitReached, queue.Enqueue(Message));
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }

        var full = CreateWithStats(Config(), new FakeTransport(), new TestLogger(), stats, capacity: 1);
        Assert.Equal(EnqueueResult.Queued, full.Enqueue(Message with { UserId = Guid.NewGuid() }));
        Assert.Equal(EnqueueResult.QueueFull, full.Enqueue(Message with { UserId = Guid.NewGuid() }));

        // Only the one delivered message is counted: the refused ones and the one still waiting in a queue without worker are not.
        Assert.Equal(1, transport.Calls);
        var row = Assert.Single(stats.Snapshot().Users);
        Assert.Equal(1, row.Sent);
        Assert.Equal(0, row.Failed);
    }

    [Fact]
    public void Enqueue_ReturnsFalse_WhenPublicUrlMissing()
    {
        var config = Config();
        config.PublicUrl = string.Empty;
        var queue = Create(config, new FakeTransport(), new TestLogger());

        Assert.Equal(EnqueueResult.NotConfigured, queue.Enqueue(Message));
    }

    [Fact]
    public void Enqueue_ReturnsLimitReached_WhenTheDailyLimitOfItsRecipientIsReached_AndWarns()
    {
        var logger = new TestLogger();
        var queue = Create(Config(2), new FakeTransport(), logger);

        Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message));
        Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message));
        Assert.Equal(EnqueueResult.LimitReached, queue.Enqueue(Message));

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("recovery", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("bob@example.com", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Enqueue_WhenFull_ReturnsQueueFull()
    {
        var queue = Create(Config(), new FakeTransport(), new TestLogger(), capacity: 2);
        Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message));
        Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message));

        Assert.Equal(EnqueueResult.QueueFull, queue.Enqueue(Message));
    }

    [Fact]
    public async Task Enqueue_WhenFull_DoesNotCountTheMessage()
    {
        var transport = new FakeTransport();
        var queue = Create(Config(3), transport, new TestLogger(), capacity: 2);
        Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message));
        Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message));
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(EnqueueResult.QueueFull, queue.Enqueue(Message));
        }

        await queue.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < 500 && transport.Succeeded < 2; i++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message));
            Assert.Equal(EnqueueResult.LimitReached, queue.Enqueue(Message));
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Enqueue_WhenFull_SecurityNotice_ReturnsQueueFull()
    {
        var queue = Create(Config(), new FakeTransport(), new TestLogger(), capacity: 2);
        Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message));
        Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message));

        Assert.Equal(EnqueueResult.QueueFull, queue.Enqueue(Message with { Template = "email-changed" }));
    }

    [Fact]
    public void Enqueue_WhenFull_LogsWithoutAddress()
    {
        var logger = new TestLogger();
        var queue = Create(Config(), new FakeTransport(), logger, capacity: 1);
        Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message));

        Assert.Equal(EnqueueResult.QueueFull, queue.Enqueue(Message));

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("recovery", warning, StringComparison.Ordinal);
        Assert.Contains(Message.UserId!.Value.ToString(), warning, StringComparison.Ordinal);
        Assert.DoesNotContain("bob@example.com", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Enqueue_NotConfigured_Warns()
    {
        var config = Config();
        config.SmtpHost = string.Empty;
        var logger = new TestLogger();
        var queue = Create(config, new FakeTransport(), logger);

        Assert.Equal(EnqueueResult.NotConfigured, queue.Enqueue(Message));

        Assert.Single(logger.Warnings);
    }

    [Fact]
    public void Enqueue_TheLimitsAreSharedByEveryKindOfMessage()
    {
        var queue = Create(Config(1), new FakeTransport(), new TestLogger());
        Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message with { Template = "recovery" }));

        Assert.Equal(EnqueueResult.LimitReached, queue.Enqueue(Message with { Template = "invitation" }));
        Assert.Equal(EnqueueResult.LimitReached, queue.Enqueue(Message with { Template = "confirm-email" }));
        Assert.Equal(EnqueueResult.LimitReached, queue.Enqueue(Message with { Template = "password-changed" }));
    }

    [Theory]
    [InlineData("email-changed")]
    [InlineData("email-change-cancelled")]
    public void Enqueue_SecurityNotices_AreNeverStoppedByTheDailyLimits(string notice)
    {
        var queue = Create(Config(1), new FakeTransport(), new TestLogger());
        Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message with { Template = "confirm-email" }));
        Assert.Equal(EnqueueResult.LimitReached, queue.Enqueue(Message with { Template = "confirm-email" }));

        Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message with { Template = notice }));
    }

    [Fact]
    public async Task EmailQueue_FailureLog_DoesNotContainTheRecipientAddress()
    {
        var transport = new FakeTransport { Throw = new InvalidOperationException("550 5.1.1 <victim@example.org>: Recipient address rejected") };
        var logger = new TestLogger();
        var queue = Create(Config(), transport, logger);
        await queue.StartAsync(CancellationToken.None);
        try
        {
            queue.Enqueue(Message);
            await logger.WaitForAsync("after 3 attempts");
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }

        Assert.DoesNotContain(logger.Lines, l => l.Contains("victim@example.org", StringComparison.Ordinal));
        Assert.Contains(logger.Lines, l => l.Contains("<address>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmailQueue_SendsQueuedMessage_AndLogsTemplateAndUser()
    {
        var transport = new FakeTransport();
        var logger = new TestLogger();
        var queue = Create(Config(), transport, logger);
        await queue.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message));
            await transport.WaitForCallsAsync(1);
            await logger.WaitForAsync("Sent recovery e-mail");
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }

        Assert.Contains(logger.Lines, l => l == "Sent recovery e-mail to user 11111111-1111-1111-1111-111111111111");
        Assert.DoesNotContain(logger.Lines, l => l.Contains("bob@example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmailQueue_FailedSend_IsRetriedThreeTimes_ThenDropped_AndLogsNoSecret()
    {
        var transport = new FakeTransport { Throw = new InvalidOperationException("login failed for password " + Password) };
        var logger = new TestLogger();
        var queue = Create(Config(), transport, logger);
        await queue.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message));
            await transport.WaitForCallsAsync(3);
            await logger.WaitForAsync("Failed to send");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }

        Assert.Equal(3, transport.Calls);
        Assert.Contains(logger.Lines, l => l.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.All(logger.Lines, l => Assert.DoesNotContain(Password, l, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, l => l.Contains("bob@example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmailQueue_RecoversAfterTransientFailure()
    {
        var transport = new FakeTransport { FailFirst = 2 };
        var queue = Create(Config(), transport, new TestLogger());
        await queue.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message));
            await transport.WaitForCallsAsync(3);
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }

        Assert.Equal(3, transport.Calls);
        Assert.Equal(1, transport.Succeeded);
    }

    [Fact]
    public async Task SendTestAsync_ReturnsNull_OnSuccess_AndRedactedError_OnFailure()
    {
        var ok = Create(Config(), new FakeTransport(), new TestLogger());
        Assert.Null(await ok.SendTestAsync("bob@example.com"));

        var bad = Create(Config(), new FakeTransport { Throw = new InvalidOperationException("auth " + Password) }, new TestLogger());
        var error = await bad.SendTestAsync("bob@example.com");
        Assert.NotNull(error);
        Assert.DoesNotContain(Password, error, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", error, StringComparison.Ordinal);

        var config = Config();
        config.SmtpHost = string.Empty;
        Assert.NotNull(await Create(config, new FakeTransport(), new TestLogger()).SendTestAsync("bob@example.com"));
    }

    [Theory]
    [InlineData(EmailLogoMode.Embedded, true, "cid:mailconcierge-logo@mailconcierge.invalid")]
    [InlineData(EmailLogoMode.Hosted, true, "https://jf.example/MailConcierge/assets/logo.png")]
    [InlineData(EmailLogoMode.None, false, "")]
    public async Task SendTestAsync_ShowsTheLogoAccordingToTheMode(EmailLogoMode mode, bool hasImage, string source)
    {
        var config = Config();
        config.EmailLogo = mode;
        var transport = new FakeTransport();

        Assert.Null(await Create(config, transport, new TestLogger()).SendTestAsync("bob@example.com"));

        var sent = Assert.Single(transport.Sent);
        if (hasImage)
        {
            Assert.Contains("<img src=\"" + source + "\"", sent.Html, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("<img", sent.Html, StringComparison.Ordinal);
        }

        // The subject and the plain text do not change with the mode.
        Assert.Equal(EmailTemplates.GetString(config.DefaultLanguage, "email.test.subject"), sent.Subject);
        Assert.Equal(EmailTemplates.GetString(config.DefaultLanguage, "email.test.body"), sent.Text);
        Assert.DoesNotContain("<img", sent.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendTestAsync_Embedded_WithAnUnreadableLogo_StillSendsWithoutImage()
    {
        using var seam = LogoSeam.Unreadable();
        var config = Config();
        var transport = new FakeTransport();

        Assert.Null(await Create(config, transport, new TestLogger()).SendTestAsync("bob@example.com"));

        var sent = Assert.Single(transport.Sent);
        Assert.DoesNotContain("<img", sent.Html, StringComparison.Ordinal);
        Assert.Contains(EmailTemplates.GetString(config.DefaultLanguage, "email.test.body").Split('\n')[0], System.Net.WebUtility.HtmlDecode(sent.Html), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmailQueue_AttemptThatNeverCompletes_EndsAtTwoMinutes_AndTheNextMessageIsDelivered()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        var transport = new HangingTransport("slow@example.com");
        var logger = new TestLogger();
        var queue = new EmailQueue(() => Config(), transport, logger, time, new[] { TimeSpan.Zero }, 100);
        await queue.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message with { To = "slow@example.com" }));
            Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message with { To = "ok@example.com" }));
            await transport.WaitForCallsAsync(1);

            time.Advance(TimeSpan.FromSeconds(119));
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.Equal(1, transport.Calls);
            Assert.DoesNotContain(logger.Lines, l => l.Contains("Failed to send", StringComparison.Ordinal));

            time.Advance(TimeSpan.FromSeconds(1));
            await logger.WaitForAsync("Failed to send recovery e-mail to user 11111111-1111-1111-1111-111111111111 (attempt 1 of 3)");
            await transport.WaitForCallsAsync(2);
            time.Advance(TimeSpan.FromMinutes(2));
            await transport.WaitForCallsAsync(3);
            time.Advance(TimeSpan.FromMinutes(2));
            await logger.WaitForAsync("after 3 attempts, dropped");
            await transport.WaitForDeliveredAsync("ok@example.com");
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }

        Assert.All(logger.Lines, l => Assert.DoesNotContain("example.com", l, StringComparison.Ordinal));
        Assert.All(logger.Lines, l => Assert.DoesNotContain(Password, l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmailQueue_ShutdownDuringAHangingAttempt_StopsAtOnce()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        var transport = new HangingTransport("slow@example.com");
        var queue = new EmailQueue(() => Config(), transport, new TestLogger(), time, new[] { TimeSpan.Zero }, 100);
        await queue.StartAsync(CancellationToken.None);
        Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message with { To = "slow@example.com" }));
        await transport.WaitForCallsAsync(1);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await queue.StopAsync(CancellationToken.None);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "Shutdown took " + watch.ElapsedMilliseconds + " ms.");
        Assert.Equal(1, transport.Calls);
    }

    private sealed class HangingTransport(string hangingAddress) : IEmailTransport
    {
        private readonly object _lock = new();
        private readonly List<string> _delivered = new();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task SendAsync(EmailMessage m, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (m.To == hangingAddress)
            {
                return Task.Delay(Timeout.Infinite, ct);
            }

            lock (_lock)
            {
                _delivered.Add(m.To);
            }

            return Task.CompletedTask;
        }

        public async Task WaitForCallsAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Calls < count && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.True(Calls >= count, $"expected {count} calls, saw {Calls}");
        }

        public async Task WaitForDeliveredAsync(string to)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (_lock)
                {
                    if (_delivered.Contains(to))
                    {
                        return;
                    }
                }

                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.Fail("The message was not delivered.");
        }
    }

    private sealed class FakeTransport : IEmailTransport
    {
        private readonly object _lock = new();
        private int _calls;
        private int _succeeded;

        public Exception? Throw { get; set; }

        public int FailFirst { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public int Succeeded => Volatile.Read(ref _succeeded);

        public List<EmailMessage> Sent { get; } = new();

        public Task SendAsync(EmailMessage m, CancellationToken ct)
        {
            int n;
            lock (_lock)
            {
                n = ++_calls;
                Sent.Add(m);
            }

            if (Throw is not null || n <= FailFirst)
            {
                throw Throw ?? new InvalidOperationException("transient");
            }

            Interlocked.Increment(ref _succeeded);
            return Task.CompletedTask;
        }

        public async Task WaitForCallsAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Calls < count && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.True(Calls >= count, $"expected {count} calls, saw {Calls}");
        }
    }

    private sealed class TestLogger : ILogger<EmailQueue>
    {
        private readonly List<string> _lines = new();
        private readonly List<string> _warnings = new();

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return _lines.ToList();
                }
            }
        }

        public IReadOnlyList<string> Warnings
        {
            get
            {
                lock (_lines)
                {
                    return _warnings.ToList();
                }
            }
        }

        public async Task WaitForAsync(string fragment)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!Lines.Any(l => l.Contains(fragment, StringComparison.Ordinal)) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            if (exception is not null)
            {
                line += " | " + exception;
            }

            lock (_lines)
            {
                _lines.Add(line);
                if (logLevel == LogLevel.Warning)
                {
                    _warnings.Add(line);
                }
            }
        }
    }
}
