using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Events.Users;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Plugin.MailConcierge.Accounts;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Data;
using Jellyfin.Plugin.MailConcierge.Email;
using Jellyfin.Plugin.MailConcierge.Security;
using Jellyfin.Plugin.MailConcierge.Seerr;
using Jellyfin.Plugin.MailConcierge.Tests.Security;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Accounts;

public sealed class EmailServiceTests : IDisposable
{
    private const string Remote = "203.0.113.7";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mailconcierge-email-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private readonly PluginConfiguration _config = new()
    {
        PublicUrl = "https://jf.example/",
        SmtpHost = "smtp.example",
        FromAddress = "jf@example.com",
        MaxRecoveryEmailsPerHour = 100,
        MaxInvitationEmailsPerHour = 100,
        MaxAccountEmailsPerHour = 100,
        ConfirmationHours = 48,
        CancelEmailChangeHours = 24,
    };

    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<ISeerrSync> _seerr = new();
    private readonly Mock<IDeviceManager> _devices = new();
    private readonly Mock<ISessionManager> _sessions = new();
    private readonly CapturingTransport _transport = new();
    private readonly MailConciergeStore _store;
    private readonly TokenService _tokens;
    private readonly EmailQueue _queue;
    private readonly EmailService _service;
    private readonly ProxyGuard _guard = Guards.Quiet();
    private readonly ServiceGate _gate;
    private readonly User _bob = new("bob", AccountPolicy.DefaultAuthProviderId, "reset") { Id = Guid.NewGuid() };

    public EmailServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new MailConciergeStore(_dir);
        _tokens = new TokenService(_dir);
        var limiter = new RateLimiter(_time);
        _queue = new EmailQueue(() => _config, _transport, limiter, NullLogger<EmailQueue>.Instance, _time, new[] { TimeSpan.Zero });
        _queue.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.FriendlyName).Returns("My Jellyfin");
        _gate = new ServiceGate(() => _config, _guard);
        _service = new EmailService(_store, _tokens, limiter, _queue, _users.Object, new SessionCloser(_devices.Object, _sessions.Object, _time, NullLogger<SessionCloser>.Instance), _seerr.Object, host.Object, () => _config, _gate, _time, NullLogger<EmailService>.Instance);
        _users.Setup(u => u.GetUserById(_bob.Id)).Returns(_bob);
        _users.Setup(u => u.AuthenticateUser("bob", "right", Remote, false)).ReturnsAsync(_bob);
        _users.Setup(u => u.AuthenticateUser("bob", "wrong", Remote, false)).ReturnsAsync((User?)null);
        _seerr.Setup(s => s.PushAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        _queue.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _queue.Dispose();
        _store.Dispose();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task RequestChange_WrongPassword_Refused_NothingStored()
    {
        var result = await _service.RequestChangeAsync(_bob.Id, "new@example.org", "wrong", Remote);

        Assert.Equal(ChangeResult.WrongPassword, result);
        Assert.Null(await _service.GetAsync(_bob.Id));
        Assert.Empty(await _store.ReadAsync(d => d.Tokens.ToList()));
        await _transport.ExpectNoneAsync();
    }

    [Fact]
    public async Task RequestChange_SendsConfirmToNewAddress_KeepsOldActive()
    {
        _config.DefaultLanguage = "fr";
        await Seed("old@example.org");

        var result = await _service.RequestChangeAsync(_bob.Id, " New@Example.org ", "right", Remote);

        Assert.Equal(ChangeResult.Ok, result);
        var record = (await _service.GetAsync(_bob.Id))!;
        Assert.Equal("old@example.org", record.Email);
        Assert.Equal("New@Example.org", record.PendingEmail);
        var mail = Assert.Single(await _transport.WaitForAsync(1));
        Assert.Equal("New@Example.org", mail.To);
        Assert.Equal("confirm-email", mail.Template);
        Assert.Contains("https://jf.example/MailConcierge/Confirm?t=", mail.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("//MailConcierge", mail.Text, StringComparison.Ordinal);
        Assert.Contains("2 novembre 2026", mail.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("Bob <bob@example.org>")]
    [InlineData("a@b")]
    [InlineData("")]
    public async Task RequestChange_InvalidAddress_Refused(string address)
    {
        Assert.Equal(ChangeResult.InvalidEmail, await _service.RequestChangeAsync(_bob.Id, address, "right", Remote));
        await _transport.ExpectNoneAsync();
    }

    [Fact]
    public async Task RequestChange_SameAsCurrent_Refused()
    {
        await Seed("bob@example.org");

        Assert.Equal(ChangeResult.InvalidEmail, await _service.RequestChangeAsync(_bob.Id, "BOB@example.org", "right", Remote));
    }

    [Fact]
    public async Task RequestChange_ConfirmationFollowsDefaultLanguage_WhateverIsStored()
    {
        _config.DefaultLanguage = "fr";
        await Seed("old@example.org", "en");

        await _service.RequestChangeAsync(_bob.Id, "new@example.org", "right", Remote);

        var mail = Assert.Single(await _transport.WaitForAsync(1));
        Assert.Contains("2 novembre 2026", mail.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PeekLanguage_FollowsDefaultLanguage_NotTheStoredOne()
    {
        await Seed("old@example.org", "en");
        await _service.RequestChangeAsync(_bob.Id, "new@example.org", "right", Remote);
        var confirm = LinkOf((await _transport.WaitForAsync(1))[0]);
        _config.DefaultLanguage = "fr";

        Assert.Equal("fr", await _service.PeekLanguageAsync(confirm, TokenPurpose.ConfirmEmail));

        _config.DefaultLanguage = "en";
        Assert.Equal("en", await _service.PeekLanguageAsync(confirm, TokenPurpose.ConfirmEmail));
        Assert.Null(await _service.PeekLanguageAsync("unknown", TokenPurpose.ConfirmEmail));
    }

    [Fact]
    public async Task RequestChange_ThirdWrongPasswordInAnHour_IsLockedWithoutReachingJellyfin()
    {
        Assert.Equal(ChangeResult.WrongPassword, await _service.RequestChangeAsync(_bob.Id, "n@example.org", "wrong", Remote));
        Assert.Equal(ChangeResult.WrongPassword, await _service.RequestChangeAsync(_bob.Id, "n@example.org", "wrong", Remote));

        Assert.Equal(ChangeResult.PasswordLocked, await _service.RequestChangeAsync(_bob.Id, "n@example.org", "wrong", Remote));
        Assert.Equal(ChangeResult.PasswordLocked, await _service.RequestChangeAsync(_bob.Id, "n@example.org", "right", Remote));
        _users.Verify(u => u.AuthenticateUser("bob", "wrong", Remote, false), Times.Exactly(2));
        _users.Verify(u => u.AuthenticateUser("bob", "right", Remote, false), Times.Never);

        _time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(ChangeResult.Ok, await _service.RequestChangeAsync(_bob.Id, "n@example.org", "right", Remote));
    }

    [Fact]
    public async Task RequestChange_ParallelWrongPasswords_ReachJellyfinAtMostTwice()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _service.RequestChangeAsync(_bob.Id, "n@example.org", "wrong", Remote)));

        _users.Verify(u => u.AuthenticateUser("bob", "wrong", Remote, false), Times.Exactly(2));
        Assert.Equal(2, results.Count(r => r == ChangeResult.WrongPassword));
    }

    [Fact]
    public async Task BothForms_OneFailureFromTheLockout_AreRefused_WithoutReachingJellyfin()
    {
        var cancel = await ConfirmedChangeAsync();
        _users.Invocations.Clear();
        _bob.LoginAttemptsBeforeLockout = 3;
        _bob.InvalidLoginAttemptCount = 2;

        Assert.Equal(ChangeResult.PasswordLocked, await _service.RequestChangeAsync(_bob.Id, "x@example.org", "wrong", Remote));
        Assert.Equal(ChangeResult.PasswordLocked, await _service.RequestChangeAsync(_bob.Id, "x@example.org", "right", Remote));
        Assert.Equal(CancelResult.Locked, await _service.CancelChangeAsync(cancel, "wrong", Remote, Remote));
        Assert.Equal(CancelResult.Locked, await _service.CancelChangeAsync(cancel, "right", Remote, Remote));

        _users.Verify(u => u.AuthenticateUser(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        Assert.Equal(2, _bob.InvalidLoginAttemptCount);
        Assert.Equal("new@example.org", (await _service.GetAsync(_bob.Id))!.Email);

        // The refusals did not use the hourly budget: once the counter is back to 0, two wrong passwords still reach Jellyfin.
        _bob.InvalidLoginAttemptCount = 0;
        Assert.Equal(ChangeResult.WrongPassword, await _service.RequestChangeAsync(_bob.Id, "x@example.org", "wrong", Remote));
        Assert.Equal(CancelResult.WrongPassword, await _service.CancelChangeAsync(cancel, "wrong", Remote, Remote));
        _users.Verify(u => u.AuthenticateUser("bob", "wrong", Remote, false), Times.Exactly(2));
    }

    [Fact]
    public async Task ThresholdThree_OneFailureAlready_OneCheck_ThenRefused()
    {
        CountWrongPasswords();
        _bob.LoginAttemptsBeforeLockout = 3;
        _bob.InvalidLoginAttemptCount = 1;

        Assert.Equal(ChangeResult.WrongPassword, await _service.RequestChangeAsync(_bob.Id, "n@example.org", "wrong", Remote));
        Assert.Equal(2, _bob.InvalidLoginAttemptCount);

        // Within the hourly limit, but one more failure would disable the account.
        Assert.Equal(ChangeResult.PasswordLocked, await _service.RequestChangeAsync(_bob.Id, "n@example.org", "wrong", Remote));
        _users.Verify(u => u.AuthenticateUser("bob", "wrong", Remote, false), Times.Once);
        Assert.Equal(2, _bob.InvalidLoginAttemptCount);
        Assert.False(_bob.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled));
    }

    [Fact]
    public async Task ThresholdThree_Cancel_OneFailureAlready_OneCheck_ThenRefused()
    {
        var cancel = await ConfirmedChangeAsync();
        CountWrongPasswords();
        _bob.LoginAttemptsBeforeLockout = 3;
        _bob.InvalidLoginAttemptCount = 1;

        Assert.Equal(CancelResult.WrongPassword, await _service.CancelChangeAsync(cancel, "wrong", Remote, Remote));
        Assert.Equal(CancelResult.Locked, await _service.CancelChangeAsync(cancel, "wrong", Remote, Remote));
        _users.Verify(u => u.AuthenticateUser("bob", "wrong", Remote, false), Times.Once);
        Assert.Equal(2, _bob.InvalidLoginAttemptCount);
    }

    [Fact]
    public async Task NoThreshold_OnlyTheHourlyLimitApplies()
    {
        CountWrongPasswords();
        _bob.LoginAttemptsBeforeLockout = null;
        _bob.InvalidLoginAttemptCount = 10;

        Assert.Equal(ChangeResult.WrongPassword, await _service.RequestChangeAsync(_bob.Id, "n@example.org", "wrong", Remote));
        Assert.Equal(ChangeResult.WrongPassword, await _service.RequestChangeAsync(_bob.Id, "n@example.org", "wrong", Remote));
        Assert.Equal(ChangeResult.PasswordLocked, await _service.RequestChangeAsync(_bob.Id, "n@example.org", "wrong", Remote));
        _users.Verify(u => u.AuthenticateUser("bob", "wrong", Remote, false), Times.Exactly(2));
        Assert.Equal(12, _bob.InvalidLoginAttemptCount);
    }

    [Fact]
    public async Task ThresholdFive_InvitedAccount_CountThree_OneCheck_CountFour_Refused()
    {
        CountWrongPasswords();
        _bob.LoginAttemptsBeforeLockout = 5;
        _bob.InvalidLoginAttemptCount = 3;

        Assert.Equal(ChangeResult.WrongPassword, await _service.RequestChangeAsync(_bob.Id, "n@example.org", "wrong", Remote));
        Assert.Equal(4, _bob.InvalidLoginAttemptCount);
        Assert.Equal(ChangeResult.PasswordLocked, await _service.RequestChangeAsync(_bob.Id, "n@example.org", "right", Remote));

        _users.Verify(u => u.AuthenticateUser("bob", "wrong", Remote, false), Times.Once);
        _users.Verify(u => u.AuthenticateUser("bob", "right", Remote, false), Times.Never);
    }

    [Fact]
    public async Task PasswordCheck_WaitsForTheAccountHeldBySignInByAddress()
    {
        var resolver = new EmailLoginResolver(_users.Object, _service, () => _config);
        var held = await resolver.LockAccountAsync(_bob.Id, TestContext.Current.CancellationToken);

        var change = _service.RequestChangeAsync(_bob.Id, "n@example.org", "right", Remote);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(change.IsCompleted);
        _users.Verify(u => u.AuthenticateUser(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);

        held.Dispose();
        Assert.Equal(ChangeResult.Ok, await change.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        _users.Verify(u => u.AuthenticateUser("bob", "right", Remote, false), Times.Once);
    }

    [Fact]
    public async Task SignInByAddress_WaitsForTheAccountHeldByAPasswordCheck()
    {
        var resolver = new EmailLoginResolver(_users.Object, _service, () => _config);
        var native = new TaskCompletionSource<User?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _users.Setup(u => u.AuthenticateUser("bob", "slow", Remote, false)).Returns(() =>
        {
            reached.TrySetResult();
            return native.Task;
        });

        var change = _service.RequestChangeAsync(_bob.Id, "n@example.org", "slow", Remote);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var signIn = resolver.LockAccountAsync(_bob.Id, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(signIn.IsCompleted);

        native.SetResult(null);
        Assert.Equal(ChangeResult.WrongPassword, await change.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        (await signIn.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Dispose();
    }

    [Fact]
    public async Task Confirm_UnknownToken_DoesNotWriteTheStore()
    {
        await _service.RequestChangeAsync(_bob.Id, "new@example.org", "right", Remote);
        var file = Path.Combine(_dir, "mailconcierge.json");
        var before = (File.GetLastWriteTimeUtc(file), new FileInfo(file).Length);
        await Task.Delay(30, TestContext.Current.CancellationToken);

        Assert.False(await _service.ConfirmAsync("unknown-token"));

        Assert.Equal(before, (File.GetLastWriteTimeUtc(file), new FileInfo(file).Length));
    }

    [Fact]
    public async Task RequestChange_OneWrongPassword_DoesNotLock()
    {
        Assert.Equal(ChangeResult.WrongPassword, await _service.RequestChangeAsync(_bob.Id, "n@example.org", "wrong", Remote));

        Assert.Equal(ChangeResult.Ok, await _service.RequestChangeAsync(_bob.Id, "n@example.org", "right", Remote));
    }

    [Fact]
    public async Task RequestChange_SixthInOneHour_IsRateLimited()
    {
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(ChangeResult.Ok, await _service.RequestChangeAsync(_bob.Id, "n" + i + "@example.org", "right", Remote));
        }

        Assert.Equal(ChangeResult.RateLimited, await _service.RequestChangeAsync(_bob.Id, "n6@example.org", "right", Remote));
    }

    [Fact]
    public async Task RequestChange_NewRequest_ReplacesPreviousConfirmationLink()
    {
        await _service.RequestChangeAsync(_bob.Id, "one@example.org", "right", Remote);
        await _service.RequestChangeAsync(_bob.Id, "two@example.org", "right", Remote);
        var mails = await _transport.WaitForAsync(2);

        Assert.False(await _service.ConfirmAsync(LinkOf(mails[0])));
        Assert.True(await _service.ConfirmAsync(LinkOf(mails[1])));
        Assert.Equal("two@example.org", (await _service.GetAsync(_bob.Id))!.Email);
    }

    [Fact]
    public async Task Confirm_SwapsAddress_SendsWarningToOld_WithCancelLink()
    {
        await Seed("old@example.org", "en");
        _config.DefaultLanguage = "fr";
        await _service.RequestChangeAsync(_bob.Id, "new@example.org", "right", Remote);
        var confirm = LinkOf((await _transport.WaitForAsync(1))[0]);

        Assert.True(await _service.ConfirmAsync(confirm));

        var record = (await _service.GetAsync(_bob.Id))!;
        Assert.Equal("new@example.org", record.Email);
        Assert.Null(record.PendingEmail);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, record.ConfirmedAt);
        var warning = (await _transport.WaitForAsync(2))[1];
        Assert.Equal("old@example.org", warning.To);
        Assert.Equal("email-changed", warning.Template);
        Assert.Contains("https://jf.example/MailConcierge/Cancel?t=", warning.Text, StringComparison.Ordinal);
        Assert.Contains("1 novembre 2026 12:00", warning.Text, StringComparison.Ordinal);
        Assert.False(await _service.ConfirmAsync(confirm));
        _seerr.Verify(s => s.PushAsync(_bob.Id), Times.Once);
    }

    [Fact]
    public async Task Confirm_FirstAddress_SendsNoWarning()
    {
        await _service.RequestChangeAsync(_bob.Id, "new@example.org", "right", Remote);

        Assert.True(await _service.ConfirmAsync(LinkOf((await _transport.WaitForAsync(1))[0])));

        await _transport.ExpectNoneAsync(1);
    }

    [Fact]
    public async Task Confirm_ExpiredOrUnknownToken_Fails()
    {
        await _service.RequestChangeAsync(_bob.Id, "new@example.org", "right", Remote);
        var confirm = LinkOf((await _transport.WaitForAsync(1))[0]);

        Assert.False(await _service.ConfirmAsync("nope"));
        _time.Advance(TimeSpan.FromHours(49));

        Assert.False(await _service.ConfirmAsync(confirm));
        Assert.Null((await _service.GetAsync(_bob.Id))!.Email);
    }

    [Fact]
    public async Task Confirm_InvalidatesResetTokens()
    {
        await Seed("old@example.org");
        var expires = _time.GetUtcNow().UtcDateTime.AddHours(1);
        await _store.WriteAsync(d =>
        {
            d.Tokens.Add(new TokenRecord("h1", TokenPurpose.ResetPin, _bob.Id, null, null, expires, 0));
            d.Tokens.Add(new TokenRecord("h2", TokenPurpose.ResetLink, _bob.Id, null, null, expires, 0));
            d.Tokens.Add(new TokenRecord("h3", TokenPurpose.ResetSession, _bob.Id, null, null, expires, 0));
        });
        await _service.RequestChangeAsync(_bob.Id, "new@example.org", "right", Remote);

        Assert.True(await _service.ConfirmAsync(LinkOf((await _transport.WaitForAsync(1))[0])));

        var purposes = await _store.ReadAsync(d => d.Tokens.Select(t => t.Purpose).ToList());
        Assert.Equal(new[] { TokenPurpose.CancelEmailChange }, purposes);
    }

    [Fact]
    public async Task Cancel_RestoresOldAddress()
    {
        await Seed("old@example.org");
        await _service.RequestChangeAsync(_bob.Id, "new@example.org", "right", Remote);
        await _service.ConfirmAsync(LinkOf((await _transport.WaitForAsync(1))[0]));
        var cancel = LinkOf((await _transport.WaitForAsync(2))[1]);
        await _store.WriteAsync(d => d.Tokens.Add(new TokenRecord("h", TokenPurpose.ResetLink, _bob.Id, null, null, _time.GetUtcNow().UtcDateTime.AddHours(1), 0)));

        Assert.Equal(CancelResult.Ok, await _service.CancelChangeAsync(cancel, "right", Remote, Remote));

        var record = (await _service.GetAsync(_bob.Id))!;
        Assert.Equal("old@example.org", record.Email);
        Assert.Null(record.PendingEmail);
        Assert.NotNull(record.ConfirmedAt);
        Assert.Empty(await _store.ReadAsync(d => d.Tokens.ToList()));
        Assert.Equal(CancelResult.Invalid, await _service.CancelChangeAsync(cancel, "right", Remote, Remote));
        _seerr.Verify(s => s.PushAsync(_bob.Id), Times.Exactly(2));
    }

    /// <summary>Makes a wrong password count as Jellyfin does: one more failure on the account.</summary>
    private void CountWrongPasswords()
        => _users.Setup(u => u.AuthenticateUser("bob", "wrong", Remote, false))
            .Callback(() => _bob.InvalidLoginAttemptCount++)
            .ReturnsAsync((User?)null);

    private async Task<string> ConfirmedChangeAsync(string oldAddress = "old@example.org")
    {
        await Seed(oldAddress);
        await _service.RequestChangeAsync(_bob.Id, "new@example.org", "right", Remote);
        await _service.ConfirmAsync(LinkOf((await _transport.WaitForAsync(1))[0]));
        return LinkOf((await _transport.WaitForAsync(2))[1]);
    }

    [Fact]
    public async Task Cancel_WrongPassword_ChangesNothing_AndKeepsTheLink()
    {
        var cancel = await ConfirmedChangeAsync();

        Assert.Equal(CancelResult.WrongPassword, await _service.CancelChangeAsync(cancel, "wrong", Remote, Remote));

        Assert.Equal("new@example.org", (await _service.GetAsync(_bob.Id))!.Email);
        Assert.Equal(CancelResult.Ok, await _service.CancelChangeAsync(cancel, "right", Remote, Remote));
    }

    [Fact]
    public async Task Cancel_TooManyWrongPasswords_IsLocked_AndNeverReachesJellyfin()
    {
        var cancel = await ConfirmedChangeAsync();
        await _service.CancelChangeAsync(cancel, "wrong", Remote, Remote);
        await _service.CancelChangeAsync(cancel, "wrong", Remote, Remote);

        Assert.Equal(CancelResult.Locked, await _service.CancelChangeAsync(cancel, "wrong", Remote, Remote));
        Assert.Equal(CancelResult.Locked, await _service.CancelChangeAsync(cancel, "right", Remote, Remote));
        _users.Verify(u => u.AuthenticateUser("bob", "wrong", Remote, false), Times.Exactly(2));
        Assert.Equal("new@example.org", (await _service.GetAsync(_bob.Id))!.Email);
    }

    [Fact]
    public async Task Cancel_WrongPasswordsAreSharedWithTheEmailChangeForm()
    {
        var cancel = await ConfirmedChangeAsync();
        await _service.RequestChangeAsync(_bob.Id, "x@example.org", "wrong", Remote);
        await _service.RequestChangeAsync(_bob.Id, "x@example.org", "wrong", Remote);

        Assert.Equal(CancelResult.Locked, await _service.CancelChangeAsync(cancel, "right", Remote, Remote));
    }

    [Fact]
    public async Task Cancel_WithoutValidLink_DoesNotCheckAnyPassword()
    {
        await ConfirmedChangeAsync();

        Assert.Equal(CancelResult.Invalid, await _service.CancelChangeAsync("nope", "right", Remote, Remote));

        _users.Verify(u => u.AuthenticateUser(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Exactly(1));
    }

    [Fact]
    public async Task Cancel_Success_NotifiesTheReplacedAddress_AndClosesSessions()
    {
        var cancel = await ConfirmedChangeAsync();
        var device = new Device(_bob.Id, "app", "1", "phone", "d1");
        _devices.Setup(d => d.GetDevices(It.Is<DeviceQuery>(q => q.UserId == _bob.Id))).Returns(new QueryResult<Device>(new[] { device }));

        Assert.Equal(CancelResult.Ok, await _service.CancelChangeAsync(cancel, "right", Remote, Remote));

        var notice = (await _transport.WaitForAsync(3))[2];
        Assert.Equal("new@example.org", notice.To);
        Assert.Equal("email-change-cancelled", notice.Template);
        _sessions.Verify(s => s.Logout(device), Times.Once);
    }

    [Fact]
    public async Task Cancel_Success_WithSessionPolicyNone_ClosesNoSession()
    {
        var cancel = await ConfirmedChangeAsync();
        _config.SessionsOnReset = SessionClosePolicy.None;

        Assert.Equal(CancelResult.Ok, await _service.CancelChangeAsync(cancel, "right", Remote, Remote));

        _sessions.Verify(s => s.Logout(It.IsAny<Device>()), Times.Never);
    }

    [Fact]
    public async Task Cancel_Link_IsValidFor24HoursByDefault()
    {
        var cancel = await ConfirmedChangeAsync();
        _time.Advance(TimeSpan.FromHours(23));
        Assert.NotNull(await _service.PeekLanguageAsync(cancel, TokenPurpose.CancelEmailChange));

        _time.Advance(TimeSpan.FromHours(2));

        Assert.Null(await _service.PeekLanguageAsync(cancel, TokenPurpose.CancelEmailChange));
        Assert.Equal(CancelResult.Invalid, await _service.CancelChangeAsync(cancel, "right", Remote, Remote));
    }

    [Fact]
    public async Task Cancel_FirstLink_AfterChainOfChanges_RestoresOriginalAddress()
    {
        await Seed("a@example.org");
        await _service.RequestChangeAsync(_bob.Id, "b@example.org", "right", Remote);
        await _service.ConfirmAsync(LinkOf((await _transport.WaitForAsync(1))[0]));
        await _service.RequestChangeAsync(_bob.Id, "c@example.org", "right", Remote);
        await _service.ConfirmAsync(LinkOf((await _transport.WaitForAsync(3))[2]));
        var mails = await _transport.WaitForAsync(4);
        var firstCancel = LinkOf(mails.Single(m => m.To == "a@example.org"));

        Assert.Equal(CancelResult.Ok, await _service.CancelChangeAsync(firstCancel, "right", Remote, Remote));

        Assert.Equal("a@example.org", (await _service.GetAsync(_bob.Id))!.Email);
    }

    [Fact]
    public async Task AdminSet_KeepsTheStoredLanguageValue()
    {
        await Seed("old@example.org", "fr");

        await _service.SetByAdminAsync(_bob.Id, "new@example.org");

        Assert.Equal("fr", (await _service.GetAsync(_bob.Id))!.Language);
    }

    [Fact]
    public async Task RequestChange_SameAddress_DoesNotUseRateLimitSlot()
    {
        await Seed("bob@example.org");
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(ChangeResult.InvalidEmail, await _service.RequestChangeAsync(_bob.Id, "bob@example.org", "right", Remote));
        }

        Assert.Equal(ChangeResult.Ok, await _service.RequestChangeAsync(_bob.Id, "new@example.org", "right", Remote));
    }

    [Fact]
    public async Task Cancel_AfterExpiry_Fails()
    {
        await Seed("old@example.org");
        await _service.RequestChangeAsync(_bob.Id, "new@example.org", "right", Remote);
        await _service.ConfirmAsync(LinkOf((await _transport.WaitForAsync(1))[0]));
        var cancel = LinkOf((await _transport.WaitForAsync(2))[1]);
        _time.Advance(TimeSpan.FromHours(25));

        Assert.Equal(CancelResult.Invalid, await _service.CancelChangeAsync(cancel, "right", Remote, Remote));
        Assert.Equal("new@example.org", (await _service.GetAsync(_bob.Id))!.Email);
    }

    [Fact]
    public async Task BadTokens_FromOneIp_AreEventuallyBlocked()
    {
        await _service.RequestChangeAsync(_bob.Id, "new@example.org", "right", Remote);
        var confirm = LinkOf((await _transport.WaitForAsync(1))[0]);
        for (var i = 0; i < 12; i++)
        {
            await _service.ConfirmAsync("bad" + i, Remote);
        }

        Assert.False(await _service.ConfirmAsync(confirm, Remote));
        Assert.True(await _service.ConfirmAsync(confirm, "198.51.100.1"));
    }

    [Fact]
    public async Task AdminSet_IsConfirmed_NoEmail()
    {
        await _service.RequestChangeAsync(_bob.Id, "pending@example.org", "right", Remote);
        await _transport.WaitForAsync(1);

        await _service.SetByAdminAsync(_bob.Id, "admin@example.org");

        var record = (await _service.GetAsync(_bob.Id))!;
        Assert.Equal("admin@example.org", record.Email);
        Assert.Null(record.PendingEmail);
        Assert.NotNull(record.ConfirmedAt);
        Assert.Equal("en", record.Language);
        Assert.Empty(await _store.ReadAsync(d => d.Tokens.ToList()));
        await _transport.ExpectNoneAsync(1);
        _seerr.Verify(s => s.PushAsync(_bob.Id), Times.Once);
    }

    [Fact]
    public async Task AdminSet_Blank_RemovesAddress_AndInvalidAddressThrows()
    {
        await Seed("old@example.org");

        await _service.SetByAdminAsync(_bob.Id, " ");

        Assert.Null((await _service.GetAsync(_bob.Id))!.Email);
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SetByAdminAsync(_bob.Id, "nope"));
    }

    [Fact]
    public async Task UserDeleted_RemovesRecords()
    {
        var other = Guid.NewGuid();
        await Seed("old@example.org");
        await _store.WriteAsync(d =>
        {
            d.Emails.Add(new UserEmail(other, "other@example.org", null, "en", null));
            d.Tokens.Add(new TokenRecord("h", TokenPurpose.ResetLink, _bob.Id, null, null, _time.GetUtcNow().UtcDateTime.AddHours(1), 0));
            d.Tokens.Add(new TokenRecord("o", TokenPurpose.ResetLink, other, null, null, _time.GetUtcNow().UtcDateTime.AddHours(1), 0));
        });

        await _service.OnEvent(new UserDeletedEventArgs(_bob));

        Assert.Null(await _service.GetAsync(_bob.Id));
        Assert.Equal(other, Assert.Single(await _store.ReadAsync(d => d.Emails.ToList())).UserId);
        Assert.Equal(other, Assert.Single(await _store.ReadAsync(d => d.Tokens.ToList())).UserId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_RequestChange_ReturnsClosed_WithoutCheckingThePassword(bool guardTripped)
    {
        await Seed("old@example.org");
        Close(guardTripped);

        Assert.Equal(ChangeResult.Closed, await _service.RequestChangeAsync(_bob.Id, "new@example.org", "right", Remote));

        _users.Verify(u => u.AuthenticateUser(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        Assert.Null((await _service.GetAsync(_bob.Id))!.PendingEmail);
        Assert.Empty(await _store.ReadAsync(d => d.Tokens.ToList()));
        await _transport.ExpectNoneAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_Confirm_ReturnsFalse_AndKeepsTheToken(bool guardTripped)
    {
        await Seed("old@example.org");
        await _service.RequestChangeAsync(_bob.Id, "new@example.org", "right", Remote);
        var confirm = LinkOf((await _transport.WaitForAsync(1))[0]);
        Close(guardTripped);

        Assert.Null(await _service.PeekLanguageAsync(confirm, TokenPurpose.ConfirmEmail));
        Assert.False(await _service.ConfirmAsync(confirm, Remote));

        var record = (await _service.GetAsync(_bob.Id))!;
        Assert.Equal("old@example.org", record.Email);
        Assert.Equal("new@example.org", record.PendingEmail);
        Assert.True(await _store.ReadAsync(d => d.Tokens.Any(t => t.Purpose == TokenPurpose.ConfirmEmail && _tokens.Matches(confirm, t.Hash))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_Cancel_ReturnsInvalid(bool guardTripped)
    {
        var cancel = await ConfirmedChangeAsync();
        Close(guardTripped);

        Assert.Null(await _service.PeekLanguageAsync(cancel, TokenPurpose.CancelEmailChange));
        Assert.Equal(CancelResult.Invalid, await _service.CancelChangeAsync(cancel, "right", Remote, Remote));

        _users.Verify(u => u.AuthenticateUser(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Exactly(1));
        Assert.Equal("new@example.org", (await _service.GetAsync(_bob.Id))!.Email);
        Assert.True(await _store.ReadAsync(d => d.Tokens.Any(t => t.Purpose == TokenPurpose.CancelEmailChange && _tokens.Matches(cancel, t.Hash))));
    }

    private void Close(bool guardTripped)
    {
        if (guardTripped)
        {
            Guards.Trip(_guard);
        }
        else
        {
            _config.Enabled = false;
        }

        Assert.False(_gate.IsOpen);
    }

    private static string LinkOf(EmailMessage m) => Regex.Match(m.Text, @"[?]t=([\w-]+)").Groups[1].Value;

    [Theory]
    [InlineData(EnqueueResult.LimitReached, ChangeResult.RateLimited)]
    [InlineData(EnqueueResult.QueueFull, ChangeResult.Unavailable)]
    public async Task RequestChange_ConfirmationNotQueued_RestoresThePreviousState(EnqueueResult refusal, ChangeResult expected)
    {
        await Seed("old@example.org");
        var previousToken = new TokenRecord(_tokens.Hash(_tokens.NewToken()), TokenPurpose.ConfirmEmail, _bob.Id, null, "first@example.org", _time.GetUtcNow().UtcDateTime.AddHours(5), 0);
        await _store.WriteAsync(d =>
        {
            d.Emails[0] = d.Emails[0] with { PendingEmail = "first@example.org" };
            d.Tokens.Add(previousToken);
        });
        var emailsBefore = await _store.ReadAsync(d => d.Emails.ToList());
        var tokensBefore = await _store.ReadAsync(d => d.Tokens.ToList());
        var (service, queue) = ServiceWithRefusingQueue(refusal);

        var result = await service.RequestChangeAsync(_bob.Id, "new@example.org", "right", Remote);

        Assert.Equal(expected, result);
        Assert.Equal(emailsBefore, await _store.ReadAsync(d => d.Emails.ToList()));
        Assert.Equal(tokensBefore, await _store.ReadAsync(d => d.Tokens.ToList()));
        queue.Dispose();
    }

    [Fact]
    public async Task RequestChange_ConfirmationNotQueued_ForAUserWithoutRecord_LeavesNoRecord()
    {
        var (service, queue) = ServiceWithRefusingQueue(EnqueueResult.LimitReached);

        Assert.Equal(ChangeResult.RateLimited, await service.RequestChangeAsync(_bob.Id, "new@example.org", "right", Remote));

        Assert.Empty(await _store.ReadAsync(d => d.Emails.ToList()));
        Assert.Empty(await _store.ReadAsync(d => d.Tokens.ToList()));
        queue.Dispose();
    }

    private (EmailService Service, EmailQueue Queue) ServiceWithRefusingQueue(EnqueueResult refusal)
    {
        // The queue is never started: a full one holds its messages, and a budget of zero refuses the first.
        var config = new PluginConfiguration
        {
            PublicUrl = _config.PublicUrl,
            SmtpHost = _config.SmtpHost,
            FromAddress = _config.FromAddress,
            ConfirmationHours = 48,
            MaxAccountEmailsPerHour = refusal == EnqueueResult.LimitReached ? 0 : 100,
        };
        var limiter = new RateLimiter(_time);
        var queue = new EmailQueue(() => config, _transport, limiter, NullLogger<EmailQueue>.Instance, _time, new[] { TimeSpan.Zero }, capacity: 1);
        if (refusal == EnqueueResult.QueueFull)
        {
            Assert.Equal(EnqueueResult.Queued, queue.Enqueue(new EmailMessage("filler@example.org", "s", "h", "t", "confirm-email", null)));
        }

        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.FriendlyName).Returns("My Jellyfin");
        var service = new EmailService(_store, _tokens, limiter, queue, _users.Object, new SessionCloser(_devices.Object, _sessions.Object, _time, NullLogger<SessionCloser>.Instance), _seerr.Object, host.Object, () => config, _gate, _time, NullLogger<EmailService>.Instance);
        return (service, queue);
    }

    private Task Seed(string email, string language = "en")
        => _store.WriteAsync(d => d.Emails.Add(new UserEmail(_bob.Id, email, null, language, _time.GetUtcNow().UtcDateTime)));

    private sealed class CapturingTransport : IEmailTransport
    {
        private readonly object _lock = new();
        private readonly List<EmailMessage> _sent = new();

        public Task SendAsync(EmailMessage m, CancellationToken ct)
        {
            lock (_lock)
            {
                _sent.Add(m);
            }

            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<EmailMessage>> WaitForAsync(int count)
        {
            for (var i = 0; i < 500; i++)
            {
                lock (_lock)
                {
                    if (_sent.Count >= count)
                    {
                        return _sent.ToList();
                    }
                }

                await Task.Delay(10);
            }

            throw new TimeoutException("Expected " + count + " e-mails.");
        }

        public async Task ExpectNoneAsync(int expected = 0)
        {
            await Task.Delay(250);
            lock (_lock)
            {
                Assert.Equal(expected, _sent.Count);
            }
        }
    }
}
