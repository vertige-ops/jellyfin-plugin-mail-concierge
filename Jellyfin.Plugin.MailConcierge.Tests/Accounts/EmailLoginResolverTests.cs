using System;
using System.IO;
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
using MediaBrowser.Controller;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Accounts;

public sealed class EmailLoginResolverTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mailconcierge-emaillogin-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Now));
    private readonly PluginConfiguration _config = new();
    private readonly MailConciergeStore _store;
    private readonly EmailQueue _queue;
    private readonly EmailService _email;
    private readonly User _alice = NewUser("alice");
    private readonly User _bob = NewUser("bob");

    public EmailLoginResolverTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new MailConciergeStore(_dir);
        var limiter = new RateLimiter(_time);
        _queue = new EmailQueue(() => _config, new Mock<IEmailTransport>().Object, limiter, NullLogger<EmailQueue>.Instance, _time);
        _email = new EmailService(
            _store,
            new TokenService(_dir),
            limiter,
            _queue,
            new Mock<IUserManager>(MockBehavior.Strict).Object,
            new SessionCloser(new Mock<IDeviceManager>().Object, new Mock<ISessionManager>().Object, _time, NullLogger<SessionCloser>.Instance),
            new Mock<ISeerrSync>().Object,
            new Mock<IServerApplicationHost>().Object,
            () => _config,
            new ServiceGate(() => _config, Guards.Quiet()),
            _time,
            NullLogger<EmailService>.Instance);
    }

    public void Dispose()
    {
        _queue.Dispose();
        _store.Dispose();
        Directory.Delete(_dir, true);
    }

    [Theory]
    [InlineData(0, null, true)]
    [InlineData(2, null, true)]
    [InlineData(3, null, false)]
    [InlineData(2, 5, true)]
    [InlineData(3, 5, false)]
    [InlineData(1, 3, true)]
    [InlineData(2, 3, false)]   // one more failure would disable the account
    [InlineData(0, 1, false)]
    [InlineData(0, 0, false)]
    [InlineData(0, -1, false)]
    public void AllowsAttempt_FollowsRules5And6(int count, int? threshold, bool expected)
        => Assert.Equal(expected, EmailLoginResolver.AllowsAttempt(count, threshold));

    [Fact]
    public void MaxFailuresByEmail_IsThree()
        => Assert.Equal(3, EmailLoginResolver.MaxFailuresByEmail);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("alice")]
    public async Task FindAccount_Null_Empty_Spaces_ReturnNull_WithoutCallingTheUserManager(string? entered)
    {
        var users = new Mock<IUserManager>(MockBehavior.Strict);
        await ConfirmAsync(_alice, "alice@example.org");

        Assert.Null(await Resolver(users).FindAccountAsync(entered));
        users.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FindAccount_NoAt_ReturnsNull()
    {
        var users = Users();
        await ConfirmAsync(_alice, "alice@example.org");

        Assert.Null(await Resolver(users).FindAccountAsync("alice.example.org"));
        users.Verify(u => u.GetUserByName(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task FindAccount_ConfirmedAddress_ReturnsItsAccount()
    {
        var users = Users();
        await ConfirmAsync(_alice, "alice@example.org");
        await ConfirmAsync(_bob, "bob@example.org");

        Assert.Equal(_alice.Id, await Resolver(users).FindAccountAsync("alice@example.org"));
    }

    [Fact]
    public async Task FindAccount_UpperCaseAndSurroundingSpaces_AreNormalized()
    {
        var users = Users();
        await ConfirmAsync(_alice, "alice@example.org");

        Assert.Equal(_alice.Id, await Resolver(users).FindAccountAsync("  Alice@Example.ORG "));

        // The user name lookup gets the raw text.
        users.Verify(u => u.GetUserByName("  Alice@Example.ORG "), Times.Once);
    }

    [Fact]
    public async Task FindAccount_ExistingUserNamedLikeTheAddress_Wins()
    {
        var users = Users();
        var named = NewUser("alice@example.org");
        users.Setup(u => u.GetUserByName("alice@example.org")).Returns(named);
        await ConfirmAsync(_alice, "alice@example.org");

        Assert.Null(await Resolver(users).FindAccountAsync("alice@example.org"));
    }

    [Theory]
    [InlineData(" a@example.org")]
    [InlineData("a@example.org ")]
    public async Task FindAccount_UserNamedLikeTheTrimmedText_Wins(string entered)
    {
        var users = Users();
        users.Setup(u => u.GetUserByName("a@example.org")).Returns(NewUser("a@example.org"));
        await ConfirmAsync(_bob, "a@example.org");

        Assert.Null(await Resolver(users).FindAccountAsync(entered));
    }

    [Theory]
    [InlineData(" a@example.org")]
    [InlineData("a@example.org ")]
    public async Task FindAccount_SurroundingSpace_WithoutSuchUser_ResolvesTheAddress(string entered)
    {
        var users = Users();
        await ConfirmAsync(_bob, "a@example.org");

        Assert.Equal(_bob.Id, await Resolver(users).FindAccountAsync(entered));
    }

    [Fact]
    public async Task FindAccount_PendingAddress_ReturnsNull()
    {
        var users = Users();
        await _store.WriteAsync(d =>
        {
            d.Emails.Add(new UserEmail(_alice.Id, null, "alice@example.org", "en", null));
            d.Emails.Add(new UserEmail(_bob.Id, "bob@example.org", null, "en", null));
        });

        Assert.Null(await Resolver(users).FindAccountAsync("alice@example.org"));
        Assert.Null(await Resolver(users).FindAccountAsync("bob@example.org"));
    }

    [Fact]
    public async Task FindAccount_SharedAddress_ReturnsNull()
    {
        var users = Users();
        await ConfirmAsync(_alice, "family@example.org");
        await ConfirmAsync(_bob, "Family@example.org");

        Assert.Null(await Resolver(users).FindAccountAsync("family@example.org"));
    }

    [Fact]
    public async Task FindAccount_UnknownAddress_ReturnsNull()
    {
        var users = Users();
        await ConfirmAsync(_alice, "alice@example.org");

        Assert.Null(await Resolver(users).FindAccountAsync("carol@example.org"));
    }

    [Fact]
    public async Task FindAccount_InvalidAddress_ReturnsNull()
    {
        var users = Users();
        await ConfirmAsync(_alice, "alice@example.org");

        Assert.Null(await Resolver(users).FindAccountAsync("a@@b"));
    }

    [Fact]
    public async Task FindAccount_UserManagerFailure_IsNotSwallowed()
    {
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserByName(It.IsAny<string>())).Throws(new InvalidOperationException("database"));
        await ConfirmAsync(_alice, "alice@example.org");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Resolver(users).FindAccountAsync("alice@example.org"));
    }

    [Fact]
    public async Task UsernameIfAllowed_EligibleAccount_ReturnsUserName()
    {
        var users = Users(_alice);
        await ConfirmAsync(_alice, "alice@example.org");

        Assert.Equal("alice", await Resolver(users).UsernameIfAllowedAsync(_alice.Id));
    }

    [Fact]
    public async Task UsernameIfAllowed_DeletedAccount_ReturnsNull()
    {
        var users = Users();
        await ConfirmAsync(_alice, "alice@example.org");

        Assert.Null(await Resolver(users).UsernameIfAllowedAsync(_alice.Id));
    }

    [Fact]
    public async Task UsernameIfAllowed_DisabledAccount_ReturnsNull()
    {
        _alice.SetPermission(PermissionKind.IsDisabled, true);
        var users = Users(_alice);
        await ConfirmAsync(_alice, "alice@example.org");

        Assert.Null(await Resolver(users).UsernameIfAllowedAsync(_alice.Id));
    }

    [Fact]
    public async Task UsernameIfAllowed_OtherProvider_ReturnsNull()
    {
        var ldap = NewUser("carol", "Jellyfin.Plugin.LDAP");
        var users = Users(ldap);
        await ConfirmAsync(ldap, "carol@example.org");

        Assert.Null(await Resolver(users).UsernameIfAllowedAsync(ldap.Id));
    }

    [Fact]
    public async Task UsernameIfAllowed_Administrator_OnlyWhenAllowAdministrators()
    {
        _alice.SetPermission(PermissionKind.IsAdministrator, true);
        var users = Users(_alice);
        await ConfirmAsync(_alice, "alice@example.org");
        var resolver = Resolver(users);

        Assert.Null(await resolver.UsernameIfAllowedAsync(_alice.Id));

        _config.AllowAdministrators = true;
        Assert.Equal("alice", await resolver.UsernameIfAllowedAsync(_alice.Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task UsernameIfAllowed_EmptyStoredPassword_ReturnsNull_ButRecoveryStillAccepts(string? stored)
    {
        _alice.Password = stored;
        var users = Users(_alice);
        await ConfirmAsync(_alice, "alice@example.org");

        Assert.Null(await Resolver(users).UsernameIfAllowedAsync(_alice.Id));
        Assert.True(AccountPolicy.IsEligible(_alice, await _email.GetAsync(_alice.Id), _config));
    }

    [Fact]
    public async Task UsernameIfAllowed_ThreeFailures_ReturnsNull()
    {
        _alice.InvalidLoginAttemptCount = 3;
        var users = Users(_alice);
        await ConfirmAsync(_alice, "alice@example.org");

        Assert.Null(await Resolver(users).UsernameIfAllowedAsync(_alice.Id));
    }

    [Fact]
    public async Task UsernameIfAllowed_ThresholdThree_TwoFailures_ReturnsNull()
    {
        _alice.InvalidLoginAttemptCount = 2;
        _alice.LoginAttemptsBeforeLockout = 3;
        var users = Users(_alice);
        await ConfirmAsync(_alice, "alice@example.org");

        Assert.Null(await Resolver(users).UsernameIfAllowedAsync(_alice.Id));
    }

    [Fact]
    public async Task UsernameIfAllowed_ReadsTheCountOnEveryCall()
    {
        var first = NewUser("alice", id: _alice.Id);
        first.InvalidLoginAttemptCount = 2;
        first.LoginAttemptsBeforeLockout = 5;
        var second = NewUser("alice", id: _alice.Id);
        second.InvalidLoginAttemptCount = 3;
        second.LoginAttemptsBeforeLockout = 5;
        var users = new Mock<IUserManager>();
        users.SetupSequence(u => u.GetUserById(_alice.Id)).Returns(first).Returns(second);
        await ConfirmAsync(_alice, "alice@example.org");
        var resolver = Resolver(users);

        Assert.Equal("alice", await resolver.UsernameIfAllowedAsync(_alice.Id));
        Assert.Null(await resolver.UsernameIfAllowedAsync(_alice.Id));
        users.Verify(u => u.GetUserById(_alice.Id), Times.Exactly(2));
    }

    [Fact]
    public async Task LockAccount_SerializesOneAccount_AndIsReleased()
    {
        var resolver = Resolver(Users());
        var held = await resolver.LockAccountAsync(_alice.Id, TestContext.Current.CancellationToken);
        var waiting = resolver.LockAccountAsync(_alice.Id, TestContext.Current.CancellationToken);
        var other = resolver.LockAccountAsync(_bob.Id, TestContext.Current.CancellationToken);

        Assert.True(other.IsCompleted);
        Assert.False(waiting.IsCompleted);

        held.Dispose();
        (await waiting).Dispose();
        (await other).Dispose();
    }

    [Fact]
    public async Task LockAccount_CancelledWhileWaiting_Throws_AndLeavesTheAccountFree()
    {
        var resolver = Resolver(Users());
        var held = await resolver.LockAccountAsync(_alice.Id, TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        var waiting = resolver.LockAccountAsync(_alice.Id, cts.Token);
        Assert.False(waiting.IsCompleted);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        held.Dispose();
        var next = resolver.LockAccountAsync(_alice.Id, TestContext.Current.CancellationToken);
        Assert.True(next.IsCompleted);
        (await next).Dispose();
    }

    private static User NewUser(string name, string provider = AccountPolicy.DefaultAuthProviderId, Guid? id = null)
        => new(name, provider, "reset") { Id = id ?? Guid.NewGuid(), Password = "stored-hash" };

    private Mock<IUserManager> Users(params User[] known)
    {
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserByName(It.IsAny<string>())).Returns((User?)null);
        foreach (var user in known)
        {
            users.Setup(u => u.GetUserById(user.Id)).Returns(user);
        }

        return users;
    }

    private EmailLoginResolver Resolver(Mock<IUserManager> users) => new(users.Object, _email, () => _config);

    private Task ConfirmAsync(User user, string address)
        => _store.WriteAsync(d => d.Emails.Add(new UserEmail(user.Id, address, null, "en", Now)));
}
