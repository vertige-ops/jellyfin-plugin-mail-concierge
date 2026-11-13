using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MailConcierge.Accounts;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Session;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Data;
using Jellyfin.Plugin.MailConcierge.Email;
using Jellyfin.Plugin.MailConcierge.Invitations;
using Jellyfin.Plugin.MailConcierge.Security;
using Jellyfin.Plugin.MailConcierge.Seerr;
using Jellyfin.Plugin.MailConcierge.Tests.Security;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Invitations;

public sealed class InvitationServiceTests : IDisposable
{
    private const string Strong = "correct horse battery";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mailconcierge-inv-" + Guid.NewGuid().ToString("N"));
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
        InvitationDays = 7,
        DefaultLanguage = "en",
    };

    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<ISessionManager> _sessions = new();
    private readonly List<string> _order = new();
    private readonly CapturingTransport _transport = new();
    private readonly MailConciergeStore _store;
    private readonly TokenService _tokens;
    private readonly EmailQueue _queue;
    private readonly EmailService _email;
    private readonly InvitationService _service;
    private readonly ProxyGuard _guard = Guards.Quiet();
    private readonly ServiceGate _gate;
    private readonly Guid _lib1 = Guid.NewGuid();
    private readonly Guid _lib2 = Guid.NewGuid();
    private readonly User _created = new("alice", AccountPolicy.DefaultAuthProviderId, "reset") { Id = Guid.NewGuid() };
    private UserPolicy? _appliedPolicy;
    private readonly List<(bool Disabled, int? Attempts)> _policySteps = new();

    public InvitationServiceTests()
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
        _email = new EmailService(_store, _tokens, limiter, _queue, _users.Object, new SessionCloser(Mock.Of<IDeviceManager>(), Mock.Of<ISessionManager>(), _time, NullLogger<SessionCloser>.Instance), new NullSeerrSync(), host.Object, () => _config, _gate, _time, NullLogger<EmailService>.Instance);
        _service = new InvitationService(_store, _tokens, limiter, _queue, _users.Object, _sessions.Object, _email, host.Object, () => _config, _gate, _time, NullLogger<InvitationService>.Instance);

        _users.Setup(u => u.CreateUserAsync("alice")).ReturnsAsync(_created);
        _users.Setup(u => u.GetUserDto(_created, null)).Returns(new UserDto { Policy = new UserPolicy { IsAdministrator = true, EnableAllFolders = true, EnableContentDeletion = true } });
        _users.Setup(u => u.UpdatePolicyAsync(_created.Id, It.IsAny<UserPolicy>())).Callback<Guid, UserPolicy>((_, p) => { _appliedPolicy = p; _policySteps.Add((p.IsDisabled, p.LoginAttemptsBeforeLockout)); _order.Add(p.IsDisabled ? "policy:disabled" : "policy:enabled"); }).Returns(Task.CompletedTask);
        _users.Setup(u => u.UpdateUserAsync(It.IsAny<User>())).Returns(Task.CompletedTask);
        _users.Setup(u => u.ChangePassword(_created.Id, It.IsAny<string>())).Callback(() => _order.Add("password")).Returns(Task.CompletedTask);
        _sessions.Setup(x => x.RevokeUserTokens(_created.Id, null)).Callback(() => _order.Add("revoke")).Returns(Task.CompletedTask);
        _users.Setup(u => u.DeleteUserAsync(_created.Id)).Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        _queue.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _queue.Dispose();
        _store.Dispose();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task Create_WithEmail_SendsInvitation()
    {
        var (inv, link, _) = await _service.CreateAsync("alice", "alice@example.org", new[] { _lib1.ToString("N") }, null, "fr");

        Assert.StartsWith("https://jf.example/MailConcierge/Accept?t=", link, StringComparison.Ordinal);
        Assert.Equal(InvitationState.Pending, inv.State);
        Assert.Equal(_time.GetUtcNow().UtcDateTime.AddDays(7), inv.ExpiresAt);
        var mail = Assert.Single(await _transport.WaitForAsync(1));
        Assert.Equal("alice@example.org", mail.To);
        Assert.Equal("invitation", mail.Template);
        Assert.Contains(link, mail.Text, StringComparison.Ordinal);
        Assert.Contains("7 novembre 2026", mail.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(link.Split('=')[1], await _store.ReadAsync(d => string.Join(' ', d.Tokens.Select(t => t.Hash))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_WithoutEmail_ReturnsLink_NoEmail()
    {
        var (inv, link, _) = await _service.CreateAsync("alice", null, new[] { _lib1.ToString("N") }, 3, "en");

        Assert.Contains("/MailConcierge/Accept?t=", link, StringComparison.Ordinal);
        Assert.Null(inv.Email);
        Assert.Equal(_time.GetUtcNow().UtcDateTime.AddDays(3), inv.ExpiresAt);
        await _transport.ExpectNoneAsync();
    }

    [Theory]
    [InlineData(null, 7)]
    [InlineData(0, 7)]
    [InlineData(-3, 7)]
    [InlineData(100000, 365)]
    public async Task Create_Days_DefaultsAndClamps(int? days, int expected)
    {
        var (inv, _, _) = await _service.CreateAsync("alice", null, Array.Empty<string>(), days, "en");

        Assert.Equal(_time.GetUtcNow().UtcDateTime.AddDays(expected), inv.ExpiresAt);
    }

    [Fact]
    public async Task Peek_DoesNotConsume()
    {
        var token = TokenOf((await _service.CreateAsync("alice", "alice@example.org", Array.Empty<string>(), null, "en")).link);

        var first = await _service.PeekAsync(token);
        var second = await _service.PeekAsync(token);

        Assert.Equal("alice", first!.ProposedUsername);
        Assert.Equal("alice@example.org", first.Email);
        Assert.NotNull(second);
        Assert.Equal(AcceptResult.Ok, await _service.AcceptAsync(token, "alice", "alice@example.org", Strong, "en"));
        Assert.Null(await _service.PeekAsync("nope"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accept_WithAvatar_SavesIt_AndNeverFailsTheAcceptance(bool saverThrows)
    {
        var saver = new Mock<AvatarSaver>(_users.Object, Mock.Of<MediaBrowser.Controller.Providers.IProviderManager>(), Mock.Of<MediaBrowser.Controller.Configuration.IServerConfigurationManager>(), NullLogger<AvatarSaver>.Instance);
        var setup = saver.Setup(a => a.SaveAsync(_created.Id, It.IsAny<byte[]>(), "image/png"));
        if (saverThrows)
        {
            setup.ThrowsAsync(new InvalidOperationException("boom"));
        }
        else
        {
            setup.ReturnsAsync(true);
        }

        var host = new Mock<IServerApplicationHost>();
        var service = new InvitationService(_store, _tokens, new RateLimiter(_time), _queue, _users.Object, _sessions.Object, _email, host.Object, () => _config, _gate, _time, NullLogger<InvitationService>.Instance, saver.Object);
        var token = TokenOf((await service.CreateAsync("alice", null, Array.Empty<string>(), null, "en")).link);
        var png = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 0x49, 0x44, 0x41, 0x54, 1, 2, 3, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0, 0, 0, 0 });

        var result = await service.AcceptAsync(token, "alice", "alice@example.org", Strong, "en", "unknown", png);

        Assert.Equal(AcceptResult.Ok, result);
        saver.Verify(a => a.SaveAsync(_created.Id, It.IsAny<byte[]>(), "image/png"), Times.Once);
        _users.Verify(u => u.DeleteUserAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Accept_WithInvalidAvatar_StillCreatesTheAccount_WithoutSaving()
    {
        var saver = new Mock<AvatarSaver>(_users.Object, Mock.Of<MediaBrowser.Controller.Providers.IProviderManager>(), Mock.Of<MediaBrowser.Controller.Configuration.IServerConfigurationManager>(), NullLogger<AvatarSaver>.Instance);
        var host = new Mock<IServerApplicationHost>();
        var service = new InvitationService(_store, _tokens, new RateLimiter(_time), _queue, _users.Object, _sessions.Object, _email, host.Object, () => _config, _gate, _time, NullLogger<InvitationService>.Instance, saver.Object);
        var token = TokenOf((await service.CreateAsync("alice", null, Array.Empty<string>(), null, "en")).link);

        var result = await service.AcceptAsync(token, "alice", "alice@example.org", Strong, "en", "unknown", "@@not base64@@");

        Assert.Equal(AcceptResult.Ok, result);
        saver.Verify(a => a.SaveAsync(It.IsAny<Guid>(), It.IsAny<byte[]>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Accept_CreatesNonAdminWithOnlyInvitedLibraries()
    {
        var token = TokenOf((await _service.CreateAsync("alice", null, new[] { _lib1.ToString("N"), _lib2.ToString() }, null, "en")).link);

        var result = await _service.AcceptAsync(token, "alice", "alice@example.org", Strong, "en");

        Assert.Equal(AcceptResult.Ok, result);
        Assert.NotNull(_appliedPolicy);
        Assert.False(_appliedPolicy.IsAdministrator);
        Assert.False(_appliedPolicy.EnableAllFolders);
        Assert.Equal(new[] { _lib1, _lib2 }, _appliedPolicy.EnabledFolders);
        Assert.True(_appliedPolicy.EnableContentDeletion);
        Assert.Equal(MailConciergePasswordResetProvider.Id, _appliedPolicy.PasswordResetProviderId);
        _users.Verify(u => u.ChangePassword(_created.Id, Strong), Times.Once);
        Assert.Equal(InvitationState.Accepted, (await _service.ListAsync()).Single().State);
        Assert.DoesNotContain(await _store.ReadAsync(d => d.Tokens.ToList()), t => t.Purpose == TokenPurpose.Invitation);
    }

    [Fact]
    public async Task Accept_SameEmailAsInvitation_IsConfirmed()
    {
        var token = TokenOf((await _service.CreateAsync("alice", "Alice@Example.org", Array.Empty<string>(), null, "en")).link);
        await _transport.WaitForAsync(1);

        Assert.Equal(AcceptResult.Ok, await _service.AcceptAsync(token, "alice", " alice@example.org ", Strong, "fr"));

        var record = (await _email.GetAsync(_created.Id))!;
        Assert.Equal("alice@example.org", record.Email);
        Assert.Null(record.PendingEmail);
        Assert.NotNull(record.ConfirmedAt);
        await _transport.ExpectNoneAsync(1);
    }

    [Fact]
    public async Task Accept_DifferentEmail_IsPending_SendsConfirm()
    {
        var token = TokenOf((await _service.CreateAsync("alice", "invited@example.org", Array.Empty<string>(), null, "en")).link);
        await _transport.WaitForAsync(1);

        Assert.Equal(AcceptResult.Ok, await _service.AcceptAsync(token, "alice", "other@example.org", Strong, "en"));

        var record = (await _email.GetAsync(_created.Id))!;
        Assert.Null(record.Email);
        Assert.Equal("other@example.org", record.PendingEmail);
        var mails = await _transport.WaitForAsync(2);
        Assert.Equal("confirm-email", mails[1].Template);
        Assert.Equal("other@example.org", mails[1].To);
        Assert.Contains("https://jf.example/MailConcierge/Confirm?t=", mails[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accept_Twice_SecondFails()
    {
        var token = TokenOf((await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en")).link);

        Assert.Equal(AcceptResult.Ok, await _service.AcceptAsync(token, "alice", "a@example.org", Strong, "en"));
        Assert.Equal(AcceptResult.InvalidToken, await _service.AcceptAsync(token, "alice", "a@example.org", Strong, "en"));
        _users.Verify(u => u.CreateUserAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Accept_Expired_Fails()
    {
        var token = TokenOf((await _service.CreateAsync("alice", null, Array.Empty<string>(), 1, "en")).link);
        _time.Advance(TimeSpan.FromDays(2));

        Assert.Equal(AcceptResult.InvalidToken, await _service.AcceptAsync(token, "alice", "a@example.org", Strong, "en"));
        Assert.Equal(InvitationState.Expired, (await _service.ListAsync()).Single().State);
        _users.Verify(u => u.CreateUserAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Accept_FailureAfterCreate_DeletesUser()
    {
        var token = TokenOf((await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en")).link);
        _users.Setup(u => u.ChangePassword(_created.Id, It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.AcceptAsync(token, "alice", "a@example.org", Strong, "en"));

        _users.Verify(u => u.DeleteUserAsync(_created.Id), Times.Once);
        Assert.Null(await _email.GetAsync(_created.Id));

        // The invitation is usable again.
        _users.Setup(u => u.ChangePassword(_created.Id, It.IsAny<string>())).Returns(Task.CompletedTask);
        Assert.Equal(AcceptResult.Ok, await _service.AcceptAsync(token, "alice", "a@example.org", Strong, "en"));
    }

    [Fact]
    public async Task Accept_AccountStaysDisabledUntilThePasswordIsSet()
    {
        var token = TokenOf((await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en")).link);

        Assert.Equal(AcceptResult.Ok, await _service.AcceptAsync(token, "alice", "a@example.org", Strong, "en"));

        Assert.Equal(2, _policySteps.Count);
        Assert.True(_policySteps[0].Disabled);
        Assert.False(_policySteps[1].Disabled);
    }

    [Fact]
    public async Task Accept_RevokesEveryTokenOfTheNewUser_AfterThePassword_BeforeTheAccountIsEnabled()
    {
        var token = TokenOf((await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en")).link);

        Assert.Equal(AcceptResult.Ok, await _service.AcceptAsync(token, "alice", "a@example.org", Strong, "en"));

        Assert.Equal(new[] { "policy:disabled", "password", "revoke", "policy:enabled" }, _order);
        _sessions.Verify(x => x.RevokeUserTokens(_created.Id, null), Times.Once);
    }

    [Fact]
    public async Task Accept_WhenRevokingFails_TheAccountStaysDisabled_IsDeleted_AndTheInvitationIsRestored()
    {
        var token = TokenOf((await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en")).link);
        _sessions.Setup(x => x.RevokeUserTokens(_created.Id, null)).ThrowsAsync(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.AcceptAsync(token, "alice", "a@example.org", Strong, "en"));

        Assert.DoesNotContain("policy:enabled", _order);
        _users.Verify(u => u.DeleteUserAsync(_created.Id), Times.Once);
        _sessions.Setup(x => x.RevokeUserTokens(_created.Id, null)).Returns(Task.CompletedTask);
        Assert.Equal(AcceptResult.Ok, await _service.AcceptAsync(token, "alice", "a@example.org", Strong, "en"));
    }

    [Fact]
    public async Task Accept_NormalizesTheUserNameToNfc_AndChecksAvailabilityWithTheSameForm()
    {
        var token = TokenOf((await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en")).link);
        var composed = "caf\u00e9";
        var decomposed = "cafe\u0301";
        _users.Setup(u => u.CreateUserAsync(composed)).ReturnsAsync(_created);

        Assert.True(await _service.IsUsernameAvailableAsync(token, decomposed));
        Assert.Equal(AcceptResult.Ok, await _service.AcceptAsync(token, decomposed, "a@example.org", Strong, "en"));

        _users.Verify(u => u.CreateUserAsync(composed), Times.Once);
        _users.Verify(u => u.CreateUserAsync(decomposed), Times.Never);
    }

    [Fact]
    public async Task Accept_UsernameOfSixtyFiveCodeUnits_IsRefused_AndSixtyFourIsAccepted()
    {
        var token = TokenOf((await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en")).link);
        var long65 = new string('a', 65);
        var ok64 = new string('a', 64);
        _users.Setup(u => u.CreateUserAsync(ok64)).ReturnsAsync(_created);

        Assert.False(await _service.IsUsernameAvailableAsync(token, long65));
        Assert.Equal(AcceptResult.InvalidUsername, await _service.AcceptAsync(token, long65, "a@example.org", Strong, "en"));
        Assert.True(await _service.IsUsernameAvailableAsync(token, ok64));
        Assert.Equal(AcceptResult.Ok, await _service.AcceptAsync(token, ok64, "a@example.org", Strong, "en"));
    }

    [Fact]
    public async Task Accept_TheLengthIsCountedAfterNormalization()
    {
        var token = TokenOf((await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en")).link);
        var decomposed64 = new string('a', 63) + "e\u0301";
        Assert.Equal(65, decomposed64.Length);
        var composed = new string('a', 63) + "\u00e9";
        _users.Setup(u => u.CreateUserAsync(composed)).ReturnsAsync(_created);

        Assert.Equal(AcceptResult.Ok, await _service.AcceptAsync(token, decomposed64, "a@example.org", Strong, "en"));
    }

    [Theory]
    [InlineData("admin\u034f")]
    [InlineData("admin\u200b")]
    [InlineData("ad\u200bmin")]
    [InlineData("admin\ufe0f")]
    [InlineData("admin\ufe00")]
    [InlineData("admin\U000e0100")]
    [InlineData("admin\U000e01ef")]
    [InlineData("admin\u202e")]
    [InlineData("admin\u2066")]
    [InlineData("admin\u200d")]
    [InlineData("admin\u115f")]
    [InlineData("admin\u1160")]
    [InlineData("admin\u3164")]
    [InlineData("admin\uffa0")]
    [InlineData("admin\u0007")]
    [InlineData("admin\ue000")]
    [InlineData("admin\u0378")]
    [InlineData("admin\ud800")]
    [InlineData("admin\u2028")]
    [InlineData("admin\u2029")]
    [InlineData("admin\U000f0000")]
    public void IsValidUsername_RefusesInvisibleAndControlCharacters(string name)
        => Assert.False(InvitationService.IsValidUsername(name));

    [Theory]
    [InlineData("\u0410\u043b\u0435\u043a\u0441\u0435\u0439")]
    [InlineData("\u0928\u092e\u0938\u094d\u0924\u0947")]
    [InlineData("\u0645\u062d\u0645\u062f")]
    [InlineData("caf\u00e9")]
    [InlineData("\u5f20\u4f1f")]
    public void IsValidUsername_AcceptsNamesInOtherScripts(string name)
        => Assert.True(InvitationService.IsValidUsername(name));

    [Fact]
    public async Task Create_ProposedName_GoesThroughTheSameRules()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync("admin\u034f", null, Array.Empty<string>(), null, "en"));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(new string('a', 65), null, Array.Empty<string>(), null, "en"));
    }

    [Fact]
    public async Task Accept_ControlOrInvisibleCharacter_IsInvalidUsername_AndConsumesNothing()
    {
        var token = TokenOf((await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en")).link);

        Assert.False(await _service.IsUsernameAvailableAsync(token, "admin\u034f"));
        Assert.Equal(AcceptResult.InvalidUsername, await _service.AcceptAsync(token, "admin\u034f", "a@example.org", Strong, "en"));
        Assert.Equal(AcceptResult.Ok, await _service.AcceptAsync(token, "alice", "a@example.org", Strong, "en"));
    }

    [Fact]
    public async Task Accept_SetsLockoutToFive()
    {
        var token = TokenOf((await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en")).link);

        Assert.Equal(AcceptResult.Ok, await _service.AcceptAsync(token, "alice", "a@example.org", Strong, "en"));

        Assert.NotEmpty(_policySteps);
        Assert.All(_policySteps, s => Assert.Equal(5, s.Attempts));
    }

    [Fact]
    public async Task Accept_DisablesLiveTvManagement()
    {
        var token = TokenOf((await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en")).link);

        Assert.Equal(AcceptResult.Ok, await _service.AcceptAsync(token, "alice", "a@example.org", Strong, "en"));

        Assert.NotNull(_appliedPolicy);
        Assert.False(_appliedPolicy.EnableLiveTvManagement);
    }

    [Theory]
    [InlineData("a@b", false)]
    [InlineData("alice@example.org", false)]
    [InlineData("@alice", false)]
    [InlineData("alice", true)]
    [InlineData("jean-luc o'neil", true)]
    [InlineData("a.b_c+d", true)]
    public void IsValidUsername_RefusesAt(string name, bool expected)
    {
        Assert.Equal(expected, InvitationService.IsValidUsername(name));
    }

    [Fact]
    public async Task Accept_UsernameWithAt_IsInvalidUsername_AndConsumesNothing()
    {
        var token = TokenOf((await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en")).link);

        Assert.Equal(AcceptResult.InvalidUsername, await _service.AcceptAsync(token, "alice@example.org", "a@example.org", Strong, "en"));
        Assert.Equal(AcceptResult.Ok, await _service.AcceptAsync(token, "alice", "a@example.org", Strong, "en"));
    }

    [Fact]
    public async Task IsUsernameAvailable_WithAt_IsFalse()
    {
        var token = TokenOf((await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en")).link);

        Assert.False(await _service.IsUsernameAvailableAsync(token, "bob@example.org"));
    }

    [Fact]
    public async Task Create_ProposedUsernameWithAt_Throws()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync("alice@example.org", null, Array.Empty<string>(), null, "en"));

        Assert.Equal("proposedUsername", ex.ParamName);
    }

    [Fact]
    public async Task Accept_CandidateNames_ShareTheBudgetOfTheAvailabilityCheck()
    {
        var token = TokenOf((await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en")).link);
        _users.Setup(u => u.GetUserByName("taken")).Returns(new User("taken", AccountPolicy.DefaultAuthProviderId, "reset"));
        for (var i = 0; i < InvitationService.MaxUsernameChecks; i++)
        {
            Assert.Equal(AcceptResult.UsernameTaken, await _service.AcceptAsync(token, "taken", "a@example.org", Strong, "en"));
        }

        // Budget spent: even a free name gets the same answer, so the existence of accounts cannot be probed.
        Assert.Equal(AcceptResult.UsernameTaken, await _service.AcceptAsync(token, "alice", "a@example.org", Strong, "en"));
    }

    [Fact]
    public async Task Accept_InvalidInputs_AreRefusedWithoutConsuming()
    {
        var token = TokenOf((await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en")).link);
        _users.Setup(u => u.GetUserByName("taken")).Returns(new User("taken", AccountPolicy.DefaultAuthProviderId, "reset"));

        Assert.Equal(AcceptResult.InvalidUsername, await _service.AcceptAsync(token, " bad/name", "a@example.org", Strong, "en"));
        Assert.Equal(AcceptResult.UsernameTaken, await _service.AcceptAsync(token, "taken", "a@example.org", Strong, "en"));
        Assert.Equal(AcceptResult.InvalidEmail, await _service.AcceptAsync(token, "alice", "nope", Strong, "en"));
        Assert.Equal(AcceptResult.WeakPassword, await _service.AcceptAsync(token, "alice", "a@example.org", "short", "en"));
        Assert.Equal(AcceptResult.Ok, await _service.AcceptAsync(token, "alice", "a@example.org", Strong, "en"));
    }

    [Fact]
    public async Task UsernameCheck_RequiresValidToken()
    {
        var token = TokenOf((await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en")).link);
        _users.Setup(u => u.GetUserByName("taken")).Returns(new User("taken", AccountPolicy.DefaultAuthProviderId, "reset"));

        Assert.False(await _service.IsUsernameAvailableAsync("invalid", "bob"));
        Assert.True(await _service.IsUsernameAvailableAsync(token, "bob"));
        Assert.False(await _service.IsUsernameAvailableAsync(token, "taken"));
        Assert.False(await _service.IsUsernameAvailableAsync(token, "bad/name"));
    }

    [Fact]
    public async Task UsernameCheck_RateLimitedPerToken()
    {
        var token = TokenOf((await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en")).link);
        var other = TokenOf((await _service.CreateAsync("carol", null, Array.Empty<string>(), null, "en")).link);

        for (var i = 0; i < 20; i++)
        {
            Assert.True(await _service.IsUsernameAvailableAsync(token, "bob"));
        }

        Assert.False(await _service.IsUsernameAvailableAsync(token, "bob"));
        Assert.True(await _service.IsUsernameAvailableAsync(other, "bob"));
        _time.Advance(TimeSpan.FromMinutes(11));
        Assert.True(await _service.IsUsernameAvailableAsync(token, "bob"));
    }

    [Fact]
    public async Task Regenerate_ReplacesTokenAndRestartsExpiry()
    {
        var (inv, link, _) = await _service.CreateAsync("alice", null, Array.Empty<string>(), 2, "en");
        _time.Advance(TimeSpan.FromDays(1));

        var (newLink, _) = await _service.RegenerateAsync(inv.Id);

        Assert.NotEqual(link, newLink);
        Assert.Null(await _service.PeekAsync(TokenOf(link)));
        Assert.NotNull(await _service.PeekAsync(TokenOf(newLink)));
        Assert.Equal(_time.GetUtcNow().UtcDateTime.AddDays(2), (await _service.ListAsync()).Single().ExpiresAt);
    }

    [Fact]
    public async Task Regenerate_WithAddress_SendsTheNewLink()
    {
        var (inv, oldLink, _) = await _service.CreateAsync("alice", "alice@example.org", Array.Empty<string>(), null, "en");
        await _transport.WaitForAsync(1);

        var (newLink, email) = await _service.RegenerateAsync(inv.Id);

        Assert.Equal(EnqueueResult.Queued, email);
        var mails = await _transport.WaitForAsync(2);
        Assert.Equal(2, mails.Count);
        var mail = mails[1];
        Assert.Equal("alice@example.org", mail.To);
        Assert.Equal("invitation", mail.Template);
        Assert.Contains(newLink, mail.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenOf(oldLink), mail.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Regenerate_WithoutAddress_SendsNothing()
    {
        var (inv, _, _) = await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en");

        var (_, email) = await _service.RegenerateAsync(inv.Id);

        Assert.Null(email);
        await _transport.ExpectNoneAsync();
    }

    [Fact]
    public async Task Regenerate_OverBudget_ReturnsLimitReached_AndStillReturnsTheLink()
    {
        _config.MaxInvitationEmailsPerHour = 1;
        var (inv, oldLink, _) = await _service.CreateAsync("alice", "alice@example.org", Array.Empty<string>(), null, "en");

        var (newLink, email) = await _service.RegenerateAsync(inv.Id);

        Assert.Equal(EnqueueResult.LimitReached, email);
        Assert.NotEqual(oldLink, newLink);
        Assert.NotNull(await _service.PeekAsync(TokenOf(newLink)));
        await _transport.ExpectNoneAsync(1);
    }

    [Fact]
    public async Task Create_WithoutPublicUrl_Refuses()
    {
        _config.PublicUrl = string.Empty;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en"));
        Assert.Empty(await _service.ListAsync());
    }

    [Fact]
    public async Task List_PrunesFinishedInvitationsAfter30Days()
    {
        var (revoked, _, _) = await _service.CreateAsync("old", null, Array.Empty<string>(), 1, "en");
        await _service.RevokeAsync(revoked.Id);
        await _service.CreateAsync("expiring", null, Array.Empty<string>(), 1, "en");
        _time.Advance(TimeSpan.FromDays(20));
        Assert.Equal(2, (await _service.ListAsync()).Count);

        _time.Advance(TimeSpan.FromDays(12));
        await _service.CreateAsync("fresh", null, Array.Empty<string>(), null, "en");

        Assert.Equal("fresh", Assert.Single(await _service.ListAsync()).ProposedUsername);
    }

    [Fact]
    public async Task Revoke_InvalidatesLink()
    {
        var (inv, link, _) = await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en");

        await _service.RevokeAsync(inv.Id);

        Assert.Equal(InvitationState.Revoked, (await _service.ListAsync()).Single().State);
        Assert.Equal(AcceptResult.InvalidToken, await _service.AcceptAsync(TokenOf(link), "alice", "a@example.org", Strong, "en"));
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Accepted")]
    [InlineData("Revoked")]
    [InlineData("Expired")]
    public async Task Erase_RemovesTheRecordAndItsTokens_WhateverTheState_AndTouchesNothingElse(string state)
    {
        var (other, otherLink, _) = await _service.CreateAsync("bob", null, Array.Empty<string>(), 30, "en");
        var (inv, link, _) = await _service.CreateAsync("alice", null, Array.Empty<string>(), 1, "en");
        switch (state)
        {
            case "Accepted":
                Assert.Equal(AcceptResult.Ok, await _service.AcceptAsync(TokenOf(link), "alice", "a@example.org", Strong, "en"));
                break;
            case "Revoked":
                await _service.RevokeAsync(inv.Id);
                break;
            case "Expired":
                _time.Advance(TimeSpan.FromDays(2));
                break;
        }

        Assert.Equal(Enum.Parse<InvitationState>(state), (await _service.ListAsync()).Single(i => i.Id == inv.Id).State);
        _users.Invocations.Clear();

        await _service.EraseAsync(inv.Id);

        var remaining = await _service.ListAsync();
        Assert.DoesNotContain(remaining, i => i.Id == inv.Id);
        Assert.Equal(other.Id, Assert.Single(remaining).Id);
        Assert.Equal(0, await _store.ReadAsync(d => d.Tokens.Count(t => t.InvitationId == inv.Id)));
        Assert.Equal(1, await _store.ReadAsync(d => d.Tokens.Count(t => t.Purpose == TokenPurpose.Invitation && t.InvitationId == other.Id)));
        Assert.NotNull(await _service.PeekAsync(TokenOf(otherLink)));
        Assert.Empty(_users.Invocations);
        if (state == "Accepted")
        {
            Assert.Equal(1, await _store.ReadAsync(d => d.Emails.Count(e => e.UserId == _created.Id)));
        }
    }

    [Fact]
    public async Task Erase_UnknownId_DoesNothing()
    {
        var (inv, link, _) = await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en");

        await _service.EraseAsync(Guid.NewGuid());

        Assert.Equal(inv.Id, Assert.Single(await _service.ListAsync()).Id);
        Assert.NotNull(await _service.PeekAsync(TokenOf(link)));
    }

    [Fact]
    public async Task Erase_LinkNoLongerOpens_AndIsAnsweredLikeAnUnknownLink()
    {
        var (inv, link, _) = await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en");
        var unknown = TokenOf(link) + "x";

        await _service.EraseAsync(inv.Id);

        Assert.Null(await _service.PeekAsync(TokenOf(link), "203.0.113.7"));
        Assert.Equal(await _service.PeekAsync(unknown, "203.0.113.8"), await _service.PeekAsync(TokenOf(link), "203.0.113.9"));
        Assert.False(await _service.IsUsernameAvailableAsync(TokenOf(link), "bob", "203.0.113.7"));
        var erased = await _service.AcceptAsync(TokenOf(link), "alice", "a@example.org", Strong, "en", "203.0.113.7");
        Assert.Equal(AcceptResult.InvalidToken, erased);
        Assert.Equal(await _service.AcceptAsync(unknown, "alice", "a@example.org", Strong, "en", "203.0.113.8"), erased);
        _users.Verify(u => u.CreateUserAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Erase_BetweenTheCheckAndTheClaimOfAnAcceptance_CreatesNoAccount()
    {
        var (inv, link, _) = await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en");

        // The user-name test runs after the link was found and before it is claimed.
        _users.Setup(u => u.GetUserByName("alice")).Returns(() =>
        {
            _service.EraseAsync(inv.Id).GetAwaiter().GetResult();
            return null;
        });

        Assert.Equal(AcceptResult.InvalidToken, await _service.AcceptAsync(TokenOf(link), "alice", "a@example.org", Strong, "en"));

        _users.Verify(u => u.CreateUserAsync(It.IsAny<string>()), Times.Never);
        Assert.Empty(await _service.ListAsync());
    }

    [Fact]
    public async Task Erase_BetweenTheClaimAndTheRollBack_DoesNotBringTheRecordOrItsTokenBack()
    {
        var (inv, link, _) = await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en");
        _users.Setup(u => u.CreateUserAsync("alice")).Returns(async () =>
        {
            await _service.EraseAsync(inv.Id);
            return _created;
        });
        _sessions.Setup(x => x.RevokeUserTokens(_created.Id, null)).ThrowsAsync(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.AcceptAsync(TokenOf(link), "alice", "a@example.org", Strong, "en"));

        _users.Verify(u => u.DeleteUserAsync(_created.Id), Times.Once);
        Assert.Empty(await _service.ListAsync());
        Assert.Equal(0, await _store.ReadAsync(d => d.Tokens.Count(t => t.Purpose == TokenPurpose.Invitation)));
        Assert.Null(await _service.PeekAsync(TokenOf(link)));
    }

    [Fact]
    public async Task Erase_BetweenTheClaimAndTheEndOfASuccessfulAcceptance_LeavesTheAccountAndNoRecord()
    {
        var (inv, link, _) = await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en");
        _users.Setup(u => u.CreateUserAsync("alice")).Returns(async () =>
        {
            await _service.EraseAsync(inv.Id);
            return _created;
        });

        Assert.Equal(AcceptResult.Ok, await _service.AcceptAsync(TokenOf(link), "alice", "a@example.org", Strong, "en"));

        _users.Verify(u => u.DeleteUserAsync(It.IsAny<Guid>()), Times.Never);
        Assert.Empty(await _service.ListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_Peek_ReturnsNull(bool guardTripped)
    {
        var token = TokenOf((await _service.CreateAsync("alice", "alice@example.org", Array.Empty<string>(), null, "en")).link);
        Close(guardTripped);

        Assert.Null(await _service.PeekAsync(token, "203.0.113.7"));
        Assert.False(await _service.IsUsernameAvailableAsync(token, "bob", "203.0.113.7"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_Accept_CreatesNoAccount(bool guardTripped)
    {
        var token = TokenOf((await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en")).link);
        Close(guardTripped);

        Assert.Equal(AcceptResult.InvalidToken, await _service.AcceptAsync(token, "alice", "alice@example.org", Strong, "en", "203.0.113.7"));

        _users.Verify(u => u.CreateUserAsync(It.IsAny<string>()), Times.Never);
        Assert.Equal(InvitationState.Pending, (await _service.ListAsync()).Single().State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_Create_Throws(bool guardTripped)
    {
        Close(guardTripped);

        await Assert.ThrowsAsync<ServiceClosedException>(() => _service.CreateAsync("alice", "alice@example.org", Array.Empty<string>(), null, "en"));

        Assert.Empty(await _service.ListAsync());
        await _transport.ExpectNoneAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_Regenerate_Throws_AndKeepsTheOldLink(bool guardTripped)
    {
        var (inv, link, _) = await _service.CreateAsync("alice", null, Array.Empty<string>(), null, "en");
        var token = TokenOf(link);
        Close(guardTripped);

        await Assert.ThrowsAsync<ServiceClosedException>(() => _service.RegenerateAsync(inv.Id));

        var kept = await _store.ReadAsync(d => d.Tokens.Any(t => t.Purpose == TokenPurpose.Invitation && t.InvitationId == inv.Id && _tokens.Matches(token, t.Hash)));
        Assert.True(kept);
        Assert.Equal(inv.ExpiresAt, (await _service.ListAsync()).Single().ExpiresAt);
    }

    [Fact]
    public async Task Closed_ThenReopened_LinkStillWorks()
    {
        var token = TokenOf((await _service.CreateAsync("alice", "alice@example.org", Array.Empty<string>(), null, "en")).link);
        _config.Enabled = false;
        for (var i = 0; i < 12; i++)
        {
            Assert.Null(await _service.PeekAsync("wrong-" + i, "203.0.113.7"));   // more than FailuresBeforeBlock
        }

        Assert.Equal(AcceptResult.InvalidToken, await _service.AcceptAsync(token, "alice", "alice@example.org", Strong, "en", "203.0.113.7"));
        _config.Enabled = true;
        Assert.Equal(AcceptResult.Ok, await _service.AcceptAsync(token, "alice", "alice@example.org", Strong, "en", "203.0.113.7"));
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

    private static string TokenOf(string link) => link[(link.IndexOf("t=", StringComparison.Ordinal) + 2)..];

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

    [Theory]
    [InlineData("bob\n")]
    [InlineData("bob\r\n")]
    [InlineData("\nbob")]
    public void IsValidUsername_RejectsLineBreaks(string name)
        => Assert.False(InvitationService.IsValidUsername(name));

    [Fact]
    public void IsValidUsername_AcceptsOrdinaryNames()
    {
        Assert.True(InvitationService.IsValidUsername("bob"));
        Assert.True(InvitationService.IsValidUsername("al'ice-b.c_d+e f g"));
    }
}
