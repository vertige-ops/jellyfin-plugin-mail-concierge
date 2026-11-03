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
        MaxRecoveryEmailsPerHour = max,
        MaxInvitationEmailsPerHour = max,
        MaxAccountEmailsPerHour = max,
    };

    private static EmailQueue Create(PluginConfiguration config, IEmailTransport transport, TestLogger logger)
        => new(() => config, transport, new RateLimiter(TimeProvider.System), logger, TimeProvider.System, new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero });

    [Fact]
    public void Enqueue_ReturnsFalse_WhenPublicUrlMissing()
    {
        var config = Config();
        config.PublicUrl = string.Empty;
        var queue = Create(config, new FakeTransport(), new TestLogger());

        Assert.Equal(EnqueueResult.NotConfigured, queue.Enqueue(Message));
    }

    [Fact]
    public void Enqueue_ReturnsLimitReached_WhenTheBudgetOfItsTypeIsExhausted_AndWarns()
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
    public void Enqueue_NotConfigured_Warns()
    {
        var config = Config();
        config.SmtpHost = string.Empty;
        var logger = new TestLogger();
        var queue = Create(config, new FakeTransport(), logger);

        Assert.Equal(EnqueueResult.NotConfigured, queue.Enqueue(Message));

        Assert.Single(logger.Warnings);
    }

    [Theory]
    [InlineData("recovery", "invitation")]
    [InlineData("recovery", "confirm-email")]
    [InlineData("confirm-email", "invitation")]
    [InlineData("password-changed", "invitation")]
    [InlineData("confirm-email", "recovery")]
    public void Enqueue_BudgetsAreSeparateByType(string exhausted, string untouched)
    {
        var queue = Create(Config(1), new FakeTransport(), new TestLogger());
        Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message with { Template = exhausted }));
        Assert.Equal(EnqueueResult.LimitReached, queue.Enqueue(Message with { Template = exhausted }));

        Assert.Equal(EnqueueResult.Queued, queue.Enqueue(Message with { Template = untouched }));
    }

    [Theory]
    [InlineData("email-changed")]
    [InlineData("email-change-cancelled")]
    public void Enqueue_SecurityNotices_AreNeverStarvedByTheAccountBudget(string notice)
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

    private sealed class FakeTransport : IEmailTransport
    {
        private readonly object _lock = new();
        private int _calls;
        private int _succeeded;

        public Exception? Throw { get; set; }

        public int FailFirst { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public int Succeeded => Volatile.Read(ref _succeeded);

        public Task SendAsync(EmailMessage m, CancellationToken ct)
        {
            int n;
            lock (_lock)
            {
                n = ++_calls;
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
