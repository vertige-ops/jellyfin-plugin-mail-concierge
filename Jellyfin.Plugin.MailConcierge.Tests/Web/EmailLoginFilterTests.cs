using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MailConcierge.Accounts;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Data;
using Jellyfin.Plugin.MailConcierge.Email;
using Jellyfin.Plugin.MailConcierge.Security;
using Jellyfin.Plugin.MailConcierge.Seerr;
using Jellyfin.Plugin.MailConcierge.Tests.Security;
using Jellyfin.Plugin.MailConcierge.Web;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Web;

public sealed class EmailLoginFilterTests : IDisposable
{
    private const string Address = "alice@example.org";
    private const string UnknownAddress = "carol@example.org";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mailconcierge-emailfilter-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _fake = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private readonly PluginConfiguration _config = new() { EmailLogin = true };
    private readonly User _alice = new("alice", AccountPolicy.DefaultAuthProviderId, "reset") { Id = Guid.NewGuid(), Password = "stored-hash" };
    private readonly Mock<IUserManager> _users = new(MockBehavior.Strict);
    private readonly MailConciergeStore _store;
    private readonly EmailQueue _queue;
    private readonly ProxyGuard _guard;
    private readonly EmailLoginResolver _resolver;
    private readonly TestLogger _logger = new();

    public EmailLoginFilterTests()
    {
        EmailLoginFilter.ResetFailureLog();
        Directory.CreateDirectory(_dir);
        _store = new MailConciergeStore(_dir);
        var limiter = new RateLimiter(_fake);
        _queue = new EmailQueue(() => _config, new Mock<IEmailTransport>().Object, limiter, NullLogger<EmailQueue>.Instance, _fake);
        _guard = Guards.Quiet();
        var email = new EmailService(
            _store,
            new TokenService(_dir),
            limiter,
            _queue,
            new Mock<IUserManager>(MockBehavior.Strict).Object,
            new SessionCloser(new Mock<IDeviceManager>().Object, new Mock<ISessionManager>().Object, _fake, NullLogger<SessionCloser>.Instance),
            new Mock<ISeerrSync>().Object,
            new Mock<IServerApplicationHost>().Object,
            () => _config,
            new ServiceGate(() => _config, _guard),
            _fake,
            NullLogger<EmailService>.Instance);
        _resolver = new EmailLoginResolver(_users.Object, email, () => _config);
        _store.WriteAsync(d => d.Emails.Add(new UserEmail(_alice.Id, Address, null, "en", _fake.GetUtcNow().UtcDateTime))).GetAwaiter().GetResult();
        _users.Setup(u => u.GetUserByName(It.IsAny<string>())).Returns((User?)null);
        _users.Setup(u => u.GetUserById(_alice.Id)).Returns(_alice);
    }

    public void Dispose()
    {
        _queue.Dispose();
        _store.Dispose();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public void GenericMessage_IsJellyfinsUnknownUserMessage_AndFloorIs700ms()
    {
        Assert.Equal("Invalid username or password entered.", EmailLoginFilter.GenericMessage);
        Assert.Equal(TimeSpan.FromMilliseconds(700), EmailLoginFilter.FailureFloor);

        // The sign-in action re-wraps SecurityException with the client address; the generic exception must not be one.
        Assert.False(typeof(SecurityException).IsAssignableFrom(typeof(AuthenticationException)));
    }

    [Fact]
    public async Task OtherAction_IsUntouched()
    {
        var request = new LoginRequest { Username = Address, Pw = "x" };
        var failure = new AuthenticationException("native");
        var calls = 0;
        var task = MakeFilter().OnActionExecutionAsync(Context(request, action: "AuthenticateWithQuickConnect"), () =>
        {
            calls++;
            return Failed(failure);
        });

        await task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.Equal(Address, request.Username);
        _users.Verify(u => u.GetUserByName(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SettingOff_DoesNothing()
    {
        _config.EmailLogin = false;
        var request = new LoginRequest { Username = Address, Pw = "x" };
        var failure = new AuthenticationException("native");

        var executed = await Invoke(request, Fails(failure), advance: TimeSpan.Zero);

        Assert.Equal(Address, request.Username);
        Assert.Same(failure, executed.Exception);
        _users.Verify(u => u.GetUserByName(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GateClosed_DoesNothing()
    {
        _config.Enabled = false;
        var disabled = new LoginRequest { Username = Address, Pw = "x" };
        var failure = new AuthenticationException("native");
        Assert.Same(failure, (await Invoke(disabled, Fails(failure), advance: TimeSpan.Zero)).Exception);
        Assert.Equal(Address, disabled.Username);

        _config.Enabled = true;
        Guards.Trip(_guard);
        var tripped = new LoginRequest { Username = Address, Pw = "x" };
        Assert.Same(failure, (await Invoke(tripped, Fails(failure), advance: TimeSpan.Zero)).Exception);
        Assert.Equal(Address, tripped.Username);

        _users.Verify(u => u.GetUserByName(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ProbeUnavailable_DoesNothing()
    {
        var request = new LoginRequest { Username = Address, Pw = "x" };
        var failure = new AuthenticationException("native");

        var executed = await Invoke(request, Fails(failure), advance: TimeSpan.Zero, filter: MakeFilter(probeAvailable: false));

        Assert.Equal(Address, request.Username);
        Assert.Same(failure, executed.Exception);
        _users.Verify(u => u.GetUserByName(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task KnownAddress_IsReplacedByTheUserName()
    {
        var request = new LoginRequest { Username = Address, Pw = "x" };
        string? seen = null;

        await Invoke(request, () =>
        {
            seen = request.Username;
            return Succeeds();
        });

        Assert.Equal("alice", seen);
        Assert.Equal("alice", request.Username);
    }

    [Fact]
    public async Task Password_IsNeverRead()
    {
        var success = new LoginRequest { Username = Address, Pw = "x" };
        await Invoke(success, () => Succeeds());
        var failure = new LoginRequest { Username = Address, Pw = "x" };
        await Invoke(failure, Fails(new AuthenticationException(EmailLoginFilter.GenericMessage)), advance: EmailLoginFilter.FailureFloor);

        Assert.Equal("alice", success.Username);
        Assert.Equal("alice", failure.Username);
        Assert.Equal(0, success.PwReads);
        Assert.Equal(0, failure.PwReads);
    }

    [Fact]
    public async Task UserNameInput_IsUntouched_AndNotDelayed()
    {
        var request = new LoginRequest { Username = "alice", Pw = "x" };
        var failure = new AuthenticationException(EmailLoginFilter.GenericMessage);

        var executed = await Invoke(request, Fails(failure), advance: TimeSpan.Zero);

        Assert.Same(failure, executed.Exception);
        Assert.Equal("alice", request.Username);
        _users.Verify(u => u.GetUserByName(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UnknownAddress_Failure_KeepsNativeException_AfterTheFloor()
    {
        var request = new LoginRequest { Username = UnknownAddress, Pw = "x" };
        var failure = new AuthenticationException(EmailLoginFilter.GenericMessage);
        var (task, called) = Start(request, Fails(failure));
        await called.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(task.IsCompleted);
        _fake.Advance(EmailLoginFilter.FailureFloor);
        var executed = await task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Same(failure, executed.Exception);
        Assert.Equal(UnknownAddress, request.Username);
    }

    [Fact]
    public async Task TranslatedFailure_Disabled_BecomesGeneric()
    {
        var executed = await Run(Address, next: Fails(new SecurityException("[203.0.113.7] The alice account is currently disabled. Please consult with your administrator.")), advance: EmailLoginFilter.FailureFloor);
        var ex = Assert.IsType<AuthenticationException>(executed.Exception);
        Assert.Equal("Invalid username or password entered.", ex.Message);
        Assert.DoesNotContain("alice", ex.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(executed.Result);
        Assert.False(executed.ExceptionHandled);
        Assert.True(executed.ExceptionDispatchInfo is null || ReferenceEquals(executed.ExceptionDispatchInfo.SourceException, ex));
    }

    [Theory]
    [InlineData("[203.0.113.7] Forbidden.")]
    [InlineData("[203.0.113.7] User is not allowed access at this time.")]
    public async Task TranslatedFailure_Forbidden_And_ParentalSchedule_BecomeGeneric(string message)
    {
        var executed = await Run(Address, next: Fails(new SecurityException(message)), advance: EmailLoginFilter.FailureFloor);

        var ex = Assert.IsType<AuthenticationException>(executed.Exception);
        Assert.Equal(EmailLoginFilter.GenericMessage, ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task TranslatedFailure_WrongPassword_IsTheSameGenericException()
    {
        var unknown = await Run(UnknownAddress, next: Fails(new AuthenticationException("Invalid username or password entered.")), advance: EmailLoginFilter.FailureFloor);
        var native = new AuthenticationException("Invalid username or password entered.");
        var translated = await Run(Address, next: Fails(native), advance: EmailLoginFilter.FailureFloor);

        Assert.NotSame(native, translated.Exception);
        Assert.Equal(unknown.Exception!.GetType(), translated.Exception!.GetType());
        Assert.Equal(unknown.Exception.Message, translated.Exception.Message);
        Assert.Null(translated.Exception.InnerException);
    }

    [Theory]
    [InlineData(Address)]
    [InlineData(UnknownAddress)]
    public async Task AddressShapedFailure_WaitsForTheFloor(string entered)
    {
        var request = new LoginRequest { Username = entered, Pw = "x" };
        var (task, called) = Start(request, Fails(new AuthenticationException(EmailLoginFilter.GenericMessage)));
        await called.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        _fake.Advance(TimeSpan.FromMilliseconds(699));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(task.IsCompleted);

        _fake.Advance(TimeSpan.FromMilliseconds(1));
        await task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(Address)]
    [InlineData(UnknownAddress)]
    public async Task Success_IsNotDelayed(string entered)
    {
        var request = new LoginRequest { Username = entered, Pw = "x" };

        var executed = await Invoke(request, () => Succeeds(), advance: TimeSpan.Zero);

        Assert.Null(executed.Exception);
    }

    [Fact]
    public async Task UnknownAddress_ArgumentException_KeepsIt_AfterTheFloor()
    {
        var failure = new ArgumentException("Value cannot be null or an empty string.", "App");

        var (executed, _) = await AnsweredAtTheFloor(UnknownAddress, Fails(failure));

        Assert.Same(failure, executed.Exception);
    }

    [Fact]
    public async Task TranslatedArgumentException_KeepsIt_AfterTheFloor()
    {
        var failure = new ArgumentException("Value cannot be null or an empty string.", "App");

        var (executed, seen) = await AnsweredAtTheFloor(Address, Fails(failure));

        Assert.Equal("alice", seen);
        Assert.Same(failure, executed.Exception);
    }

    [Fact]
    public async Task TranslatedOtherException_BecomesGeneric_AfterTheFloor()
    {
        // Stands for a database error in the failure counter update, which happens only for a known account.
        var (executed, seen) = await AnsweredAtTheFloor(Address, Fails(new InvalidOperationException("database is locked")));

        Assert.Equal("alice", seen);
        var ex = Assert.IsType<AuthenticationException>(executed.Exception);
        Assert.Equal(EmailLoginFilter.GenericMessage, ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task UnknownAddress_OtherException_KeepsIt_AfterTheFloor()
    {
        var failure = new InvalidOperationException("other");

        var (executed, _) = await AnsweredAtTheFloor(UnknownAddress, Fails(failure));

        Assert.Same(failure, executed.Exception);
    }

    [Theory]
    [InlineData(Address)]
    [InlineData(UnknownAddress)]
    public async Task ErrorResultWithoutException_IsKept_AfterTheFloor(string entered)
    {
        var result = new StatusCodeResult(400);

        var (executed, _) = await AnsweredAtTheFloor(entered, () => Answered(result));

        Assert.Null(executed.Exception);
        Assert.Same(result, executed.Result);
    }

    [Theory]
    [InlineData(Address)]
    [InlineData(UnknownAddress)]
    public async Task AbortedRequest_Cancellation_IsNotDelayed(string entered)
    {
        using var aborted = new CancellationTokenSource();
        var request = new LoginRequest { Username = entered, Pw = "x" };
        var failure = new OperationCanceledException(aborted.Token);
        string? seen = null;

        // No clock advance: nobody is listening, so the answer is not held back.
        var (task, _) = Start(
            request,
            async () =>
            {
                seen = request.Username;
                await aborted.CancelAsync();
                return await Failed(failure);
            },
            configure: http => http.RequestAborted = aborted.Token);
        var executed = await task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(entered == Address ? "alice" : entered, seen);
        Assert.Same(failure, executed.Exception);
    }

    [Fact]
    public async Task Cancellation_WhileTheRequestIsAlive_WaitsAndBecomesGeneric()
    {
        var (executed, _) = await AnsweredAtTheFloor(Address, Fails(new OperationCanceledException()));

        Assert.Equal(EmailLoginFilter.GenericMessage, Assert.IsType<AuthenticationException>(executed.Exception).Message);
    }

    [Fact]
    public async Task SuccessResult_IsNotDelayed()
    {
        var executed = await Run(Address, next: () => Answered(new OkResult()), advance: TimeSpan.Zero);

        Assert.Null(executed.Exception);
        Assert.IsType<OkResult>(executed.Result);
    }

    [Fact]
    public async Task HandledException_IsUntouched()
    {
        var failure = new SecurityException("[203.0.113.7] Forbidden.");

        var executed = await Run(Address, next: () => Failed(failure, handled: true), advance: TimeSpan.Zero);

        Assert.Same(failure, executed.Exception);
        Assert.True(executed.ExceptionHandled);
    }

    [Fact]
    public async Task ObserveIsNeverCalled()
    {
        var request = new LoginRequest { Username = Address, Pw = "x" };
        var context = Context(request);
        context.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.2");
        context.HttpContext.Request.Headers["X-Forwarded-For"] = "203.0.113.5";

        var task = MakeFilter().OnActionExecutionAsync(context, Fails(new AuthenticationException(EmailLoginFilter.GenericMessage)));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        _fake.Advance(EmailLoginFilter.FailureFloor);
        await task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal("alice", request.Username);
        Assert.False(_guard.Tripped);
    }

    [Fact]
    public async Task ResolverThrows_NativePathUntouched()
    {
        _users.Setup(u => u.GetUserByName("alice@example.org")).Throws(new InvalidOperationException("db"));
        var request = new LoginRequest { Username = "alice@example.org", Pw = "x" };
        var calls = 0;
        await Invoke(request, () => { calls++; return Succeeds(); });
        Assert.Equal(1, calls);
        Assert.Equal("alice@example.org", request.Username);
    }

    [Fact]
    public async Task PluginFailure_IsLoggedOnce_WithoutTheAddress()
    {
        _users.Setup(u => u.GetUserByName(Address)).Throws(new InvalidOperationException("db failed for alice@example.org"));
        var filter = MakeFilter();

        await Invoke(new LoginRequest { Username = Address, Pw = "x" }, () => Succeeds(), filter: filter);
        await Invoke(new LoginRequest { Username = Address, Pw = "x" }, () => Succeeds(), filter: MakeFilter());

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(nameof(InvalidOperationException), entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", entry.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.org", entry.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PluginFailureUnderTheLock_ReleasesTheLock_AndCallsNextOnceUnchanged()
    {
        // The first read (pre-check, outside the lock) succeeds; the second, under the lock, fails.
        var reads = 0;
        _users.Setup(u => u.GetUserById(_alice.Id)).Returns(() => ++reads == 2 ? throw new InvalidOperationException("db") : _alice);
        var request = new LoginRequest { Username = Address, Pw = "x" };
        var calls = 0;
        string? seen = null;

        await Invoke(request, () =>
        {
            calls++;
            seen = request.Username;
            return Succeeds();
        });

        Assert.Equal(2, reads);
        Assert.Equal(1, calls);
        Assert.Equal(Address, seen);
        var entry = Assert.Single(_logger.Entries);
        Assert.Contains(_alice.Id.ToString(), entry.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alice", entry.Text.Replace(_alice.Id.ToString(), string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
        var handle = _resolver.LockAccountAsync(_alice.Id, TestContext.Current.CancellationToken);
        Assert.True(handle.IsCompleted);
        (await handle).Dispose();
    }

    [Fact]
    public async Task NextThrows_Translated_WaitsForTheFloor_ThenThrowsTheGenericException()
    {
        var calls = 0;

        var thrown = await ThrownAtTheFloor(Address, () =>
        {
            calls++;
            throw new InvalidOperationException("pipeline");
        });

        Assert.Equal(1, calls);
        var ex = Assert.IsType<AuthenticationException>(thrown);
        Assert.Equal(EmailLoginFilter.GenericMessage, ex.Message);
        Assert.Null(ex.InnerException);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task NextThrows_Translated_ArgumentException_IsKept()
    {
        var failure = new ArgumentException("Value cannot be null or an empty string.", "App");

        var thrown = await ThrownAtTheFloor(Address, () => throw failure);

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task NextThrows_Untranslated_WaitsForTheFloor_ThenThrowsTheSameException()
    {
        var failure = new InvalidOperationException("pipeline");
        var calls = 0;

        var thrown = await ThrownAtTheFloor(UnknownAddress, () =>
        {
            calls++;
            throw failure;
        });

        Assert.Equal(1, calls);
        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task NextThrows_AbortedRequest_IsNotDelayed()
    {
        using var aborted = new CancellationTokenSource();
        var request = new LoginRequest { Username = Address, Pw = "x" };
        var failure = new OperationCanceledException(aborted.Token);

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() => MakeFilter()
            .OnActionExecutionAsync(Context(request, configure: http => http.RequestAborted = aborted.Token), async () =>
            {
                await aborted.CancelAsync();
                throw failure;
            })
            .WaitAsync(Timeout, TestContext.Current.CancellationToken));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task NoAt_AnyFailure_IsNotDelayed()
    {
        var failure = new InvalidOperationException("other");
        Assert.Same(failure, (await Run("alice", next: Fails(failure), advance: TimeSpan.Zero)).Exception);

        var result = new StatusCodeResult(400);
        Assert.Same(result, (await Run("alice", next: () => Answered(result), advance: TimeSpan.Zero)).Result);

        var thrown = new ArgumentException("pipeline");
        Assert.Same(thrown, await Assert.ThrowsAsync<ArgumentException>(() => MakeFilter()
            .OnActionExecutionAsync(Context(new LoginRequest { Username = "alice", Pw = "x" }), () => throw thrown)
            .WaitAsync(Timeout, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task EmptyStoredPassword_IsNotTranslated_AndNeverWaitsForTheLock()
    {
        _alice.Password = string.Empty;
        using var held = await _resolver.LockAccountAsync(_alice.Id, TestContext.Current.CancellationToken);
        var request = new LoginRequest { Username = Address, Pw = string.Empty };
        string? seen = null;

        // No clock advance: the request must reach Jellyfin without waiting for the held account.
        var (task, called) = Start(request, () =>
        {
            seen = request.Username;
            return Succeeds();
        });
        await called.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(Address, seen);
        Assert.Equal(Address, request.Username);
        Assert.Equal(0, request.PwReads);
        _users.Verify(u => u.GetUserById(_alice.Id), Times.Once);
    }

    [Fact]
    public async Task PluginFailure_OneLinePerTypePerHour()
    {
        var fails = new InvalidOperationException("db");
        _users.Setup(u => u.GetUserByName(Address)).Throws(fails);

        await Invoke(new LoginRequest { Username = Address, Pw = "x" }, () => Succeeds());
        await Invoke(new LoginRequest { Username = Address, Pw = "x" }, () => Succeeds());
        Assert.Single(_logger.Entries);

        _users.Setup(u => u.GetUserByName(Address)).Throws(new FormatException("other"));
        await Invoke(new LoginRequest { Username = Address, Pw = "x" }, () => Succeeds());
        Assert.Equal(2, _logger.Entries.Count);
        Assert.Contains(nameof(FormatException), _logger.Entries[1].Text, StringComparison.Ordinal);

        _users.Setup(u => u.GetUserByName(Address)).Throws(fails);
        _fake.Advance(TimeSpan.FromMinutes(59));
        await Invoke(new LoginRequest { Username = Address, Pw = "x" }, () => Succeeds());
        Assert.Equal(2, _logger.Entries.Count);

        _fake.Advance(TimeSpan.FromMinutes(1));
        await Invoke(new LoginRequest { Username = Address, Pw = "x" }, () => Succeeds());
        Assert.Equal(3, _logger.Entries.Count);
        Assert.Contains(nameof(InvalidOperationException), _logger.Entries[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PluginFailure_TheCapCountsTypesSeenInTheLastHour()
    {
        var types = typeof(object).Assembly.GetExportedTypes()
            .Where(t => typeof(Exception).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null)
            .Take(33)
            .ToList();
        Assert.Equal(33, types.Count);

        foreach (var type in types.Take(32))
        {
            _users.Setup(u => u.GetUserByName(Address)).Throws((Exception)Activator.CreateInstance(type)!);
            await Invoke(new LoginRequest { Username = Address, Pw = "x" }, () => Succeeds());
        }

        Assert.Equal(32, _logger.Entries.Count);

        // Within the hour a 33rd type is not logged.
        _users.Setup(u => u.GetUserByName(Address)).Throws((Exception)Activator.CreateInstance(types[32])!);
        await Invoke(new LoginRequest { Username = Address, Pw = "x" }, () => Succeeds());
        Assert.Equal(32, _logger.Entries.Count);

        // Once the hour has passed the old entries no longer count.
        _fake.Advance(TimeSpan.FromHours(1));
        await Invoke(new LoginRequest { Username = Address, Pw = "x" }, () => Succeeds());
        Assert.Equal(33, _logger.Entries.Count);
        Assert.Contains(types[32].FullName!, _logger.Entries[32].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddressRemovedWhileWaitingForTheLock_IsNotTranslated()
    {
        // The second lookup, under the lock, no longer finds the address: it was removed meanwhile.
        var lookups = 0;
        _users.Setup(u => u.GetUserByName(Address)).Returns(() =>
        {
            if (++lookups == 2)
            {
                _store.WriteAsync(d => d.Emails.RemoveAll(e => e.UserId == _alice.Id)).GetAwaiter().GetResult();
            }

            return null;
        });
        var request = new LoginRequest { Username = Address, Pw = "x" };
        string? seen = null;

        await Invoke(request, () =>
        {
            seen = request.Username;
            return Succeeds();
        });

        Assert.Equal(2, lookups);
        Assert.Equal(Address, seen);

        // Read once by the pre-check outside the lock, never under it once the address is gone.
        _users.Verify(u => u.GetUserById(It.IsAny<Guid>()), Times.Once);
        Assert.Empty(_logger.Entries);
        var handle = _resolver.LockAccountAsync(_alice.Id, TestContext.Current.CancellationToken);
        Assert.True(handle.IsCompleted);
        (await handle).Dispose();
    }

    [Fact]
    public async Task ParallelTranslatedRequests_SecondSeesTheNewCount()
    {
        // alice: threshold 5, InvalidLoginAttemptCount 2. The first request holds `next` open on a TaskCompletionSource.
        // The second passes the pre-check (count 2) and waits for the account; no fake time passes, so its wait does not end.
        // While the first is open, the second request must not have called its `next`.
        // The first fails: the test raises alice's count to 3, then releases it.
        // The second request then reaches Jellyfin untranslated: its Username is still the address.
        _alice.LoginAttemptsBeforeLockout = 5;
        _alice.InvalidLoginAttemptCount = 2;
        var hold = new TaskCompletionSource<ActionExecutedContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new LoginRequest { Username = Address, Pw = "x" };
        var (firstTask, firstCalled) = Start(first, () => hold.Task);
        await firstCalled.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal("alice", first.Username);

        var second = new LoginRequest { Username = Address, Pw = "x" };
        string? secondSeen = null;
        var secondCalls = 0;
        var (secondTask, secondCalled) = Start(second, () =>
        {
            secondCalls++;
            secondSeen = second.Username;
            return Failed(new AuthenticationException(EmailLoginFilter.GenericMessage));
        });
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(secondCalled.IsCompleted);

        _alice.InvalidLoginAttemptCount = 3;
        hold.SetResult(await Failed(new AuthenticationException(EmailLoginFilter.GenericMessage)));

        // The lock is released as soon as the native call returns, not after the floor of the first request.
        await secondCalled.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.False(firstTask.IsCompleted);
        Assert.Equal(Address, secondSeen);

        _fake.Advance(EmailLoginFilter.FailureFloor);
        var firstExecuted = await firstTask.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await secondTask.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var secondExecuted = await secondTask.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(EmailLoginFilter.GenericMessage, Assert.IsType<AuthenticationException>(firstExecuted.Exception).Message);
        Assert.Equal(1, secondCalls);
        Assert.Equal(Address, second.Username);
        Assert.Equal(EmailLoginFilter.GenericMessage, Assert.IsType<AuthenticationException>(secondExecuted.Exception).Message);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task OverTheLimitAccount_NeverWaitsForTheLock()
    {
        using var held = await _resolver.LockAccountAsync(_alice.Id, TestContext.Current.CancellationToken);
        _alice.InvalidLoginAttemptCount = 3;
        var request = new LoginRequest { Username = Address, Pw = "x" };
        var calls = 0;
        string? seen = null;

        // No clock advance: the request must reach Jellyfin without waiting for the held account.
        var (task, called) = Start(request, () =>
        {
            calls++;
            seen = request.Username;
            return Succeeds();
        });
        await called.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
        Assert.Equal(Address, seen);
        Assert.Equal(Address, request.Username);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public void LockWait_IsWellUnderTheFloor()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(175), EmailLoginFilter.LockWait);
        Assert.True(EmailLoginFilter.LockWait < EmailLoginFilter.FailureFloor);
    }

    [Fact]
    public async Task HeldAccount_GoesOnUntranslated_AfterLockWait()
    {
        using var held = await _resolver.LockAccountAsync(_alice.Id, TestContext.Current.CancellationToken);
        var request = new LoginRequest { Username = Address, Pw = "x" };
        var failure = new AuthenticationException(EmailLoginFilter.GenericMessage);
        var calls = 0;
        string? seen = null;
        var (task, called) = Start(request, () =>
        {
            calls++;
            seen = request.Username;
            return Failed(failure);
        });

        // Lets the filter reach the wait for the account; the time limit itself is driven by the fake clock only.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        _fake.Advance(EmailLoginFilter.LockWait - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(called.IsCompleted);

        _fake.Advance(TimeSpan.FromMilliseconds(1));
        await called.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.Equal(Address, seen);
        Assert.Empty(_logger.Entries);

        // The floor counts from the start of the attempt, not from the end of the wait.
        _fake.Advance(EmailLoginFilter.FailureFloor - EmailLoginFilter.LockWait - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(task.IsCompleted);

        _fake.Advance(TimeSpan.FromMilliseconds(1));
        var executed = await task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Same(failure, executed.Exception);
        Assert.Equal(1, calls);
        Assert.Equal(Address, request.Username);
        Assert.Equal(0, request.PwReads);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task AbortedRequest_GoesOnUntranslated_WithoutErrorLine()
    {
        using var held = await _resolver.LockAccountAsync(_alice.Id, TestContext.Current.CancellationToken);
        using var aborted = new CancellationTokenSource();
        var request = new LoginRequest { Username = Address, Pw = "x" };
        var calls = 0;
        string? seen = null;
        var (task, called) = Start(
            request,
            () =>
            {
                calls++;
                seen = request.Username;
                return Succeeds();
            },
            configure: http => http.RequestAborted = aborted.Token);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(called.IsCompleted);
        await aborted.CancelAsync();
        await task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
        Assert.Equal(Address, seen);
        Assert.Equal(Address, request.Username);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task NotTranslatedAfterTheSecondCheck_ReleasesTheAccountBeforeTheNativeCall()
    {
        // Threshold 5: the pre-check reads a count of 2 (allowed), the check under the lock reads 3 (no longer allowed).
        _alice.LoginAttemptsBeforeLockout = 5;
        var reads = 0;
        _users.Setup(u => u.GetUserById(_alice.Id)).Returns(() =>
        {
            _alice.InvalidLoginAttemptCount = ++reads == 1 ? 2 : 3;
            return _alice;
        });
        var request = new LoginRequest { Username = Address, Pw = "x" };
        var calls = 0;
        string? seen = null;
        var accountFree = false;

        await Invoke(request, async () =>
        {
            calls++;
            seen = request.Username;
            using var shortWait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                (await _resolver.LockAccountAsync(_alice.Id, shortWait.Token)).Dispose();
                accountFree = true;
            }
            catch (OperationCanceledException)
            {
                accountFree = false;
            }

            return await Succeeds();
        });

        Assert.Equal(2, reads);
        Assert.Equal(1, calls);
        Assert.Equal(Address, seen);
        Assert.True(accountFree);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task KnownButNotEligibleAddress_KeepsNativeException_AfterTheFloor()
    {
        _alice.SetPermission(PermissionKind.IsDisabled, true);
        var request = new LoginRequest { Username = Address, Pw = "x" };
        var failure = new SecurityException("[203.0.113.7] The alice account is currently disabled. Please consult with your administrator.");
        var calls = 0;
        var (task, called) = Start(request, () =>
        {
            calls++;
            return Failed(failure);
        });
        await called.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        _fake.Advance(TimeSpan.FromMilliseconds(699));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(task.IsCompleted);

        _fake.Advance(TimeSpan.FromMilliseconds(1));
        var executed = await task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Same(failure, executed.Exception);
        Assert.Equal(1, calls);
        Assert.Equal(Address, request.Username);
        Assert.Empty(_logger.Entries);
    }

    private static Task<ActionExecutedContext> Succeeds() => Failed(null);

    private static Task<ActionExecutedContext> Failed(Exception? exception, bool handled = false)
    {
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ControllerActionDescriptor());
        return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object())
        {
            Exception = exception,
            ExceptionHandled = handled,
        });
    }

    private static ActionExecutionDelegate Fails(Exception exception) => () => Failed(exception);

    private static Task<ActionExecutedContext> Answered(IActionResult result)
    {
        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ControllerActionDescriptor());
        return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object()) { Result = result });
    }

    private static ActionExecutingContext Context(LoginRequest request, string action = EmailLoginProbe.ActionName, Action<HttpContext>? configure = null)
    {
        var http = new DefaultHttpContext();
        configure?.Invoke(http);
        http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        var descriptor = new ControllerActionDescriptor { ControllerName = EmailLoginProbe.ControllerName, ActionName = action };
        var actionContext = new ActionContext(http, new RouteData(), descriptor);
        var args = new Dictionary<string, object?> { ["request"] = request };
        return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), args, new object());
    }

    private static IActionDescriptorCollectionProvider Provider(bool available)
    {
        var actions = available
            ? new ActionDescriptor[]
            {
                new ControllerActionDescriptor
                {
                    ControllerName = EmailLoginProbe.ControllerName,
                    ActionName = EmailLoginProbe.ActionName,
                    Parameters = new List<ParameterDescriptor> { new() { Name = "request", ParameterType = typeof(LoginRequest) } },
                },
            }
            : Array.Empty<ActionDescriptor>();
        var provider = new Mock<IActionDescriptorCollectionProvider>();
        provider.SetupGet(p => p.ActionDescriptors).Returns(new ActionDescriptorCollection(actions, 1));
        return provider.Object;
    }

    /// <summary>Runs the filter and checks that it ends at the floor, not 1 ms sooner; returns the outcome and the name Jellyfin got.</summary>
    private async Task<(ActionExecutedContext Executed, string? Seen)> AnsweredAtTheFloor(string entered, ActionExecutionDelegate next)
    {
        var request = new LoginRequest { Username = entered, Pw = "x" };
        string? seen = null;
        var (task, called) = Start(request, () =>
        {
            seen = request.Username;
            return next();
        });
        await called.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await ReachTheFloor(task);
        var executed = await task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(0, request.PwReads);
        return (executed, seen);
    }

    /// <summary>Runs the filter with a native call that throws, checks that it ends at the floor, and returns what it threw.</summary>
    private async Task<Exception> ThrownAtTheFloor(string entered, ActionExecutionDelegate next)
    {
        var request = new LoginRequest { Username = entered, Pw = "x" };
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = MakeFilter().OnActionExecutionAsync(Context(request), () =>
        {
            called.TrySetResult();
            return next();
        });
        await called.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await ReachTheFloor(task);
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => task.WaitAsync(Timeout, TestContext.Current.CancellationToken));

        // The account is free again.
        var handle = _resolver.LockAccountAsync(_alice.Id, TestContext.Current.CancellationToken);
        Assert.True(handle.IsCompleted);
        (await handle).Dispose();
        return thrown;
    }

    private async Task ReachTheFloor(Task task)
    {
        _fake.Advance(EmailLoginFilter.FailureFloor - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(task.IsCompleted);
        _fake.Advance(TimeSpan.FromMilliseconds(1));
    }

    private Task<ActionExecutedContext> Run(string username, ActionExecutionDelegate next, TimeSpan advance)
        => Invoke(new LoginRequest { Username = username, Pw = "x" }, next, advance);

    /// <summary>Runs the filter, advances the fake clock once the native call has been made, and returns what the native call returned.</summary>
    private async Task<ActionExecutedContext> Invoke(LoginRequest request, ActionExecutionDelegate next, TimeSpan? advance = null, EmailLoginFilter? filter = null)
    {
        var (task, called) = Start(request, next, filter);
        await Task.WhenAny(called, task).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        if (advance is { } step && step > TimeSpan.Zero)
        {
            _fake.Advance(step);
        }

        return await task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    /// <summary>Starts the filter; the first task gives what the native call returned, the second completes when it is called.</summary>
    private (Task<ActionExecutedContext> Task, Task Called) Start(LoginRequest request, ActionExecutionDelegate next, EmailLoginFilter? filter = null, Action<HttpContext>? configure = null)
    {
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ActionExecutedContext? executed = null;
        var run = (filter ?? MakeFilter()).OnActionExecutionAsync(Context(request, configure: configure), async () =>
        {
            // The native call is started before it is signalled, so that what it records is visible once `called` completes.
            var pending = next();
            called.TrySetResult();
            executed = await pending;
            return executed;
        });
        return (Completed(run), called.Task);

        async Task<ActionExecutedContext> Completed(Task filterTask)
        {
            await filterTask;
            return executed!;
        }
    }

    private EmailLoginFilter MakeFilter(bool probeAvailable = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Func<PluginConfiguration>>(() => _config);
        services.AddSingleton(new ServiceGate(() => _config, _guard));
        services.AddSingleton(new EmailLoginProbe(Provider(probeAvailable)));
        services.AddSingleton(_resolver);
        services.AddSingleton<TimeProvider>(_fake);
        return new EmailLoginFilter(services.BuildServiceProvider(), _logger);
    }

    private sealed class LoginRequest
    {
        private string? _pw;

        public int PwReads { get; private set; }

        public string? Username { get; set; }

        public string? Pw
        {
            get
            {
                PwReads++;
                return _pw;
            }

            set => _pw = value;
        }
    }

    private sealed class TestLogger : ILogger<EmailLoginFilter>
    {
        private readonly List<(LogLevel Level, string Text)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Text)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception) + " " + exception));
            }
        }
    }
}
