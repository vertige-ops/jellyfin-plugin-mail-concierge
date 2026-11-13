using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Plugin.MailConcierge.Accounts;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Data;
using Jellyfin.Plugin.MailConcierge.Email;
using Jellyfin.Plugin.MailConcierge.Security;
using Jellyfin.Plugin.MailConcierge.Seerr;
using Jellyfin.Plugin.MailConcierge.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Configuration;
using Microsoft.AspNetCore.Http;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Accounts;

public sealed class RecoveryServiceTests : IDisposable
{
    private const string Ip = "203.0.113.7";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mailconcierge-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private readonly PluginConfiguration _config = new()
    {
        PublicUrl = "https://jf.example/",
        SmtpHost = "smtp.example",
        FromAddress = "jf@example.com",
        MaxRecoveryEmailsPerHour = 100,
        MaxInvitationEmailsPerHour = 100,
        MaxAccountEmailsPerHour = 100,
        MaxForgotRequestsPerIpPer15Min = 100,
    };

    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<IDeviceManager> _devices = new();
    private readonly Mock<ISessionManager> _sessions = new();
    private readonly CapturingTransport _transport = new();
    private readonly MailConciergeStore _store;
    private readonly TokenService _tokens;
    private readonly EmailQueue _queue;
    private readonly RecoveryService _service;
    private readonly ProxyGuard _guard;
    private readonly User _bob = Make("bob");
    private readonly EmailLoginResolver _resolver;
    private readonly TestLogger _logger = new();

    public RecoveryServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new MailConciergeStore(_dir);
        _tokens = new TokenService(_dir);
        var limiter = new RateLimiter(_time);
        _queue = new EmailQueue(() => _config, _transport, limiter, NullLogger<EmailQueue>.Instance, _time, new[] { TimeSpan.Zero });
        _queue.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        var serverConfig = new Mock<IServerConfigurationManager>();
        serverConfig.Setup(c => c.GetConfiguration("network")).Returns(new NetworkConfiguration { KnownProxies = Array.Empty<string>() });
        _guard = new ProxyGuard(serverConfig.Object);
        var guard = _guard;
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.FriendlyName).Returns("My Jellyfin");
        var closer = new SessionCloser(_devices.Object, _sessions.Object, _time, NullLogger<SessionCloser>.Instance);
        var email = new EmailService(_store, _tokens, limiter, _queue, _users.Object, closer, Mock.Of<ISeerrSync>(), host.Object, () => _config, new ServiceGate(() => _config, guard), _time, NullLogger<EmailService>.Instance);
        _resolver = new EmailLoginResolver(_users.Object, email, () => _config);
        _service = new RecoveryService(
            _store, _tokens, limiter, _queue, new ServiceGate(() => _config, guard), _users.Object, _resolver, closer, host.Object, () => _config, _time, _logger, TimeSpan.Zero);

        _users.Setup(u => u.GetUserByName("bob")).Returns(_bob);
        _users.Setup(u => u.GetUserById(_bob.Id)).Returns(_bob);
        _users.Setup(u => u.ChangePassword(It.IsAny<Guid>(), It.IsAny<string>())).Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        _queue.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _queue.Dispose();
        _store.Dispose();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task Request_UsesDefaultLanguage_EvenWhenTheStoredLanguageDiffers()
    {
        await Confirm(_bob);
        _config.DefaultLanguage = "fr";

        await _service.RequestAsync("bob", Ip);

        var mail = Assert.Single(await _transport.WaitForAsync(1));
        Assert.Contains("Bonjour", mail.Text, StringComparison.Ordinal);
        _config.DefaultLanguage = "en";
        var link = LinkOf(mail);
        Assert.Equal("en", await _service.PeekLinkLanguageAsync(link));
        _config.DefaultLanguage = "fr";
        Assert.Equal("fr", await _service.PeekLinkLanguageAsync(link));
        Assert.Null(await _service.PeekLinkLanguageAsync("unknown"));
    }

    [Fact]
    public async Task Request_UnknownUser_SendsNothing()
    {
        await Confirm(_bob);

        await _service.RequestAsync("nobody", Ip);

        await _transport.ExpectNoneAsync();
    }

    [Fact]
    public async Task Request_Eligible_SendsOneEmailWithPinAndLink()
    {
        await Confirm(_bob);

        await _service.RequestAsync("bob", Ip);

        var mail = Assert.Single(await _transport.WaitForAsync(1));
        Assert.Equal("bob@example.org", mail.To);
        Assert.Equal("recovery", mail.Template);
        Assert.Matches(@"\b\d{4}-\d{4}\b", mail.Text);
        Assert.Contains("https://jf.example/MailConcierge/Reset?t=", mail.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("//MailConcierge", mail.Text, StringComparison.Ordinal);
        Assert.Matches(@"\d{4} 12:30", mail.Text);
        Assert.Contains("PIN code", mail.Text, StringComparison.Ordinal);
        Assert.Contains("bob", mail.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_NotEligible_SendsNothing()
    {
        await _store.WriteAsync(d => d.Emails.Add(new UserEmail(_bob.Id, "bob@example.org", null, "en", null)));

        await _service.RequestAsync("bob", Ip);

        await _transport.ExpectNoneAsync();
    }

    [Fact]
    public async Task Request_FourthInOneHour_SendsNothing()
    {
        await Confirm(_bob);

        for (var i = 0; i < 4; i++)
        {
            await _service.RequestAsync("bob", Ip);
        }

        Assert.Equal(3, (await _transport.WaitForAsync(3)).Count);
        await _transport.ExpectNoneAsync(3);
    }

    [Fact]
    public async Task Request_InvalidatesPreviousPin()
    {
        await Confirm(_bob);
        await _service.RequestAsync("bob", Ip);
        await _service.RequestAsync("bob", Ip);
        var mails = await _transport.WaitForAsync(2);

        Assert.Null(await _service.RedeemPinAsync("bob", PinOf(mails[0]), Ip));
        Assert.Null(await _service.RedeemLinkAsync(LinkOf(mails[0]), Ip));
        Assert.NotNull(await _service.RedeemPinAsync("bob", PinOf(mails[1]), Ip));
        Assert.Equal(0, await _store.ReadAsync(d => d.Tokens.Count(t => t.Purpose == TokenPurpose.ResetLink)));
    }

    [Fact]
    public async Task RedeemPin_WrongUsername_Fails()
    {
        var alice = Make("alice");
        _users.Setup(u => u.GetUserByName("alice")).Returns(alice);
        await Confirm(_bob);
        await Confirm(alice);
        await _service.RequestAsync("bob", Ip);
        var pin = PinOf((await _transport.WaitForAsync(1))[0]);

        Assert.Null(await _service.RedeemPinAsync("alice", pin, Ip));
        Assert.NotNull(await _service.RedeemPinAsync("bob", pin, Ip));
    }

    [Fact]
    public async Task RedeemPin_FiveFailures_DeletesPin()
    {
        await Confirm(_bob);
        await _service.RequestAsync("bob", Ip);
        var pin = PinOf((await _transport.WaitForAsync(1))[0]);
        var wrong = pin == "0000-0000" ? "1111-1111" : "0000-0000";

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(await _service.RedeemPinAsync("bob", wrong, Ip));
        }

        Assert.Null(await _service.RedeemPinAsync("bob", pin, Ip));
        Assert.Equal(0, await _store.ReadAsync(d => d.Tokens.Count(t => t.Purpose == TokenPurpose.ResetPin)));
    }

    [Fact]
    public async Task RedeemPin_ParallelGuesses_NeverExceedMaxFailures()
    {
        await Confirm(_bob);
        await _service.RequestAsync("bob", Ip);
        var pin = PinOf((await _transport.WaitForAsync(1))[0]);
        var wrong = pin == "0000-0000" ? "1111-1111" : "0000-0000";

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => _service.RedeemPinAsync("bob", wrong, "10.0.0." + i)));

        Assert.Equal(0, await _store.ReadAsync(d => d.Tokens.Count(t => t.Purpose == TokenPurpose.ResetPin)));
        Assert.Null(await _service.RedeemPinAsync("bob", pin, Ip));
    }

    [Fact]
    public async Task RedeemPin_TenFailuresFromOneIp_BlocksThatIp()
    {
        await Confirm(_bob);
        await _service.RequestAsync("bob", Ip);
        var pin = PinOf((await _transport.WaitForAsync(1))[0]);

        for (var i = 0; i < 10; i++)
        {
            await _service.RedeemPinAsync("nobody", "0000-0000", Ip);
        }

        Assert.Null(await _service.RedeemPinAsync("bob", pin, Ip));
        Assert.NotNull(await _service.RedeemPinAsync("bob", pin, "198.51.100.1"));
    }

    [Fact]
    public async Task RedeemPin_Expired_Fails()
    {
        await Confirm(_bob);
        await _service.RequestAsync("bob", Ip);
        var pin = PinOf((await _transport.WaitForAsync(1))[0]);

        _time.Advance(TimeSpan.FromMinutes(31));

        Assert.Null(await _service.RedeemPinAsync("bob", pin, Ip));
    }

    [Fact]
    public async Task RedeemLink_ValidToken_GivesSession_AndSecondUseFails()
    {
        await Confirm(_bob);
        await _service.RequestAsync("bob", Ip);
        var link = LinkOf((await _transport.WaitForAsync(1))[0]);

        Assert.Null(await _service.RedeemLinkAsync("garbage", Ip));
        var session = await _service.RedeemLinkAsync(link, Ip);

        Assert.NotNull(session);
        Assert.Null(await _service.RedeemLinkAsync(link, Ip));
    }

    [Fact]
    public async Task IsLinkValid_FreshToken_IsTrue_AndNotConsumed()
    {
        await Confirm(_bob);
        await _service.RequestAsync("bob", Ip);
        var link = LinkOf((await _transport.WaitForAsync(1))[0]);

        Assert.True(await _service.IsLinkValidAsync(link));
        Assert.True(await _service.IsLinkValidAsync(link));
        Assert.NotNull(await _service.RedeemLinkAsync(link, Ip));
    }

    [Fact]
    public async Task IsLinkValid_UsedExpiredGarbageAndIneligible_AreFalse()
    {
        await Confirm(_bob);
        await _service.RequestAsync("bob", Ip);
        var link = LinkOf((await _transport.WaitForAsync(1))[0]);

        Assert.False(await _service.IsLinkValidAsync("garbage"));
        Assert.False(await _service.IsLinkValidAsync(string.Empty));
        _config.Enabled = false;
        Assert.False(await _service.IsLinkValidAsync(link));
        _config.Enabled = true;
        Assert.True(await _service.IsLinkValidAsync(link));
        _bob.SetPermission(PermissionKind.IsAdministrator, true);
        Assert.False(await _service.IsLinkValidAsync(link));
        _bob.SetPermission(PermissionKind.IsAdministrator, false);
        Assert.NotNull(await _service.RedeemLinkAsync(link, Ip));
        Assert.False(await _service.IsLinkValidAsync(link));
    }

    [Fact]
    public async Task IsLinkValid_AfterExpiry_IsFalse()
    {
        await Confirm(_bob);
        await _service.RequestAsync("bob", Ip);
        var link = LinkOf((await _transport.WaitForAsync(1))[0]);

        _time.Advance(TimeSpan.FromMinutes(31));

        Assert.False(await _service.IsLinkValidAsync(link));
    }

    [Fact]
    public async Task Complete_ChangesPassword_DeletesTokens_SendsNotice()
    {
        await Confirm(_bob);
        await _service.RequestAsync("bob", Ip);
        var pin = PinOf((await _transport.WaitForAsync(1))[0]);
        var session = await _service.RedeemPinAsync("bob", pin, Ip);
        _devices.Setup(d => d.GetDevices(It.IsAny<DeviceQuery>())).Returns(new QueryResult<Device>(Array.Empty<Device>()));

        Assert.True(await _service.CompleteAsync(session!, "correct horse", Ip));

        _users.Verify(u => u.ChangePassword(_bob.Id, "correct horse"), Times.Once);
        Assert.Equal(0, await _store.ReadAsync(d => d.Tokens.Count(t => t.UserId == _bob.Id)));
        var mails = await _transport.WaitForAsync(2);
        Assert.Equal("password-changed", mails[1].Template);
        Assert.False(await _service.CompleteAsync(session!, "another one!", Ip));
        _devices.Verify(d => d.GetDevices(It.Is<DeviceQuery>(q => q.UserId == _bob.Id)), Times.Once);
    }

    [Fact]
    public async Task Complete_UserBecameAdmin_ChangesNothing()
    {
        await Confirm(_bob);
        await _service.RequestAsync("bob", Ip);
        var session = await _service.RedeemPinAsync("bob", PinOf((await _transport.WaitForAsync(1))[0]), Ip);
        _bob.SetPermission(PermissionKind.IsAdministrator, true);

        Assert.False(await _service.CompleteAsync(session!, "long enough", Ip));
        _users.Verify(u => u.ChangePassword(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        Assert.False(await _service.CompleteAsync(session!, "long enough", Ip));
    }

    [Fact]
    public async Task Redeem_And_Complete_WhenDisabled_Refuse()
    {
        await Confirm(_bob);
        await _service.RequestAsync("bob", Ip);
        var mail = (await _transport.WaitForAsync(1))[0];
        var session = await _service.RedeemPinAsync("bob", PinOf(mail), Ip);
        _config.Enabled = false;

        Assert.Null(await _service.RedeemPinAsync("bob", PinOf(mail), Ip));
        Assert.Null(await _service.RedeemLinkAsync(LinkOf(mail), Ip));
        Assert.False(await _service.CompleteAsync(session!, "long enough", Ip));
        _users.Verify(u => u.ChangePassword(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Request_WhenProxyGuardTripped_SendsNothing()
    {
        await Confirm(_bob);
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.2");
        ctx.Request.Headers["X-Forwarded-For"] = "203.0.113.9";
        _guard.Observe(ctx);
        Assert.True(_guard.Tripped);

        await _service.RequestAsync("bob", Ip);

        await _transport.ExpectNoneAsync();
    }

    [Fact]
    public async Task Request_WhenEmailNotConfigured_KeepsPreviousPin()
    {
        await Confirm(_bob);
        await _service.RequestAsync("bob", Ip);
        var pin = PinOf((await _transport.WaitForAsync(1))[0]);
        _config.SmtpHost = string.Empty;

        await _service.RequestAsync("bob", Ip);

        _config.SmtpHost = "smtp.example";
        Assert.NotNull(await _service.RedeemPinAsync("bob", pin, Ip));
    }

    [Fact]
    public async Task Complete_PinNeverBecomesPassword()
    {
        await Confirm(_bob);
        await _service.RequestAsync("bob", Ip);
        var pin = PinOf((await _transport.WaitForAsync(1))[0]);
        var session = await _service.RedeemPinAsync("bob", pin, Ip);
        _devices.Setup(d => d.GetDevices(It.IsAny<DeviceQuery>())).Returns(new QueryResult<Device>(Array.Empty<Device>()));

        await _service.CompleteAsync(session!, "brand new pass", Ip);

        _users.Verify(u => u.ChangePassword(It.IsAny<Guid>(), pin), Times.Never);
        _users.Verify(u => u.ChangePassword(It.IsAny<Guid>(), pin.Replace("-", string.Empty, StringComparison.Ordinal)), Times.Never);
        _users.Verify(u => u.ChangePassword(_bob.Id, "brand new pass"), Times.Once);
    }

    [Fact]
    public async Task Complete_TooShortPassword_ChangesNothing_AndKeepsSession()
    {
        await Confirm(_bob);
        await _service.RequestAsync("bob", Ip);
        var session = await _service.RedeemPinAsync("bob", PinOf((await _transport.WaitForAsync(1))[0]), Ip);
        _devices.Setup(d => d.GetDevices(It.IsAny<DeviceQuery>())).Returns(new QueryResult<Device>(Array.Empty<Device>()));

        Assert.False(await _service.CompleteAsync(session!, "short", Ip));
        _users.Verify(u => u.ChangePassword(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        Assert.True(await _service.CompleteAsync(session!, "long enough", Ip));
    }

    [Fact]
    public async Task Complete_ParallelCalls_ChangePasswordOnce()
    {
        await Confirm(_bob);
        await _service.RequestAsync("bob", Ip);
        var session = await _service.RedeemPinAsync("bob", PinOf((await _transport.WaitForAsync(1))[0]), Ip);
        _devices.Setup(d => d.GetDevices(It.IsAny<DeviceQuery>())).Returns(new QueryResult<Device>(Array.Empty<Device>()));

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => _service.CompleteAsync(session!, "password " + i, Ip)));

        Assert.Equal(1, results.Count(r => r));
        _users.Verify(u => u.ChangePassword(It.IsAny<Guid>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task SessionCloser_InactiveOnly_LogsOutOldDevicesOnly()
    {
        var old = new Device(_bob.Id, "app", "1", "old", "d1") { DateLastActivity = _time.GetUtcNow().UtcDateTime.AddDays(-40) };
        var recent = new Device(_bob.Id, "app", "1", "recent", "d2") { DateLastActivity = _time.GetUtcNow().UtcDateTime.AddDays(-2) };
        _devices.Setup(d => d.GetDevices(It.Is<DeviceQuery>(q => q.UserId == _bob.Id))).Returns(new QueryResult<Device>(new[] { old, recent }));
        var closer = new SessionCloser(_devices.Object, _sessions.Object, _time, NullLogger<SessionCloser>.Instance);

        await closer.CloseAsync(_bob.Id, new PluginConfiguration { SessionsOnReset = SessionClosePolicy.InactiveSinceDays, SessionInactiveDays = 30 });

        _sessions.Verify(s => s.Logout(old), Times.Once);
        _sessions.Verify(s => s.Logout(recent), Times.Never);
    }

    [Fact]
    public async Task SessionCloser_All_LogsOutEveryDevice_None_LogsOutNone()
    {
        var a = new Device(_bob.Id, "app", "1", "a", "d1");
        var b = new Device(_bob.Id, "app", "1", "b", "d2");
        _devices.Setup(d => d.GetDevices(It.IsAny<DeviceQuery>())).Returns(new QueryResult<Device>(new[] { a, b }));
        var closer = new SessionCloser(_devices.Object, _sessions.Object, _time, NullLogger<SessionCloser>.Instance);

        await closer.CloseAsync(_bob.Id, new PluginConfiguration { SessionsOnReset = SessionClosePolicy.None });
        _sessions.Verify(s => s.Logout(It.IsAny<Device>()), Times.Never);

        await closer.CloseAsync(_bob.Id, new PluginConfiguration { SessionsOnReset = SessionClosePolicy.All });
        _sessions.Verify(s => s.Logout(It.IsAny<Device>()), Times.Exactly(2));
    }

    [Fact]
    public void Neutral_UsesLocalizedSentence_AndExpiryRoundedDownToTheMinute()
    {
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 31, 12, 0, 45, TimeSpan.Zero));
        var responses = new ForgotPasswordResponses(() => _config, _time);

        var en = responses.Neutral("en");
        var fr = responses.Neutral("fr");

        Assert.Equal("If this account has an e-mail address, a code has just been sent to it.", en.PinFile);
        Assert.Equal("Si ce compte a une adresse e-mail, un code vient d’y être envoyé.", fr.PinFile);
        Assert.Equal(MediaBrowser.Model.Users.ForgotPasswordAction.PinCode, en.Action);
        Assert.Equal(new DateTime(2026, 10, 31, 12, 30, 0, DateTimeKind.Utc), en.PinExpirationDate);
    }

    [Fact]
    public async Task Complete_UnknownSession_DoesNotWriteTheStore()
    {
        await Confirm(_bob);
        File.Delete(Path.Combine(_dir, "mailconcierge.json"));

        Assert.False(await _service.CompleteAsync("no-such-session", "long enough", Ip));

        Assert.False(File.Exists(Path.Combine(_dir, "mailconcierge.json")));
    }

    [Fact]
    public async Task Complete_UnknownSessions_BlockTheIp_ThenEvenAValidSessionIsRefused()
    {
        await Confirm(_bob);
        await _service.RequestAsync("bob", Ip);
        var session = await _service.RedeemPinAsync("bob", PinOf((await _transport.WaitForAsync(1))[0]), "198.51.100.9");
        for (var i = 0; i < 12; i++)
        {
            Assert.False(await _service.CompleteAsync("bad-" + i, "long enough", Ip));
        }

        Assert.False(await _service.CompleteAsync(session!, "long enough", Ip));
        _users.Verify(u => u.ChangePassword(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        Assert.True(await _service.CompleteAsync(session!, "long enough", "198.51.100.10"));
    }

    [Fact]
    public async Task RedeemPin_WithoutActivePin_DoesNotWriteTheStore()
    {
        await Confirm(_bob);
        File.Delete(Path.Combine(_dir, "mailconcierge.json"));

        Assert.Null(await _service.RedeemPinAsync("bob", "1234-5678", Ip));
        Assert.Null(await _service.RedeemPinAsync("nobody", "1234-5678", Ip));

        Assert.False(File.Exists(Path.Combine(_dir, "mailconcierge.json")));
    }

    [Fact]
    public async Task RedeemLink_UnknownLink_DoesNotWriteTheStore()
    {
        await Confirm(_bob);
        File.Delete(Path.Combine(_dir, "mailconcierge.json"));

        Assert.Null(await _service.RedeemLinkAsync("no-such-link", Ip));

        Assert.False(File.Exists(Path.Combine(_dir, "mailconcierge.json")));
    }

    [Fact]
    public async Task Request_AddressReplacedBetweenReadAndWrite_MailsTheNewAddressOnly()
    {
        await Confirm(_bob);
        var service = ServiceWithHookAfterAccountLimit(() => ReplaceEmail(_bob, "bob.new@example.org", confirmed: true));

        await service.RequestAsync("bob", Ip);

        var mail = Assert.Single(await _transport.WaitForAsync(1));
        Assert.Equal("bob.new@example.org", mail.To);
        await _transport.ExpectNoneAsync(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_AddressRemovedOrUnconfirmedBetweenReadAndWrite_QueuesNothingAndStoresNoToken(bool removed)
    {
        await Confirm(_bob);
        var service = ServiceWithHookAfterAccountLimit(() =>
        {
            if (removed)
            {
                _store.WriteAsync(d => d.Emails.RemoveAll(e => e.UserId == _bob.Id)).GetAwaiter().GetResult();
            }
            else
            {
                ReplaceEmail(_bob, "bob.new@example.org", confirmed: false);
            }
        });

        await service.RequestAsync("bob", Ip);

        await _transport.ExpectNoneAsync();
        var tokens = await _store.ReadAsync(d => d.Tokens.Count(t => t.UserId == _bob.Id && t.Purpose is TokenPurpose.ResetPin or TokenPurpose.ResetLink));
        Assert.Equal(0, tokens);
    }

    [Fact]
    public async Task RedeemPin_TakesAtLeastTheResponseFloor_WhateverTheOutcome()
    {
        await Confirm(_bob);
        var limiter = new RateLimiter(_time);
        var service = new RecoveryService(
            _store, _tokens, limiter, _queue, new ServiceGate(() => _config, _guard), _users.Object, _resolver, new SessionCloser(_devices.Object, _sessions.Object, _time, NullLogger<SessionCloser>.Instance),
            Mock.Of<IServerApplicationHost>(), () => _config, _time, NullLogger<RecoveryService>.Instance, TimeSpan.FromMilliseconds(300));

        foreach (var name in new[] { "bob", "nobody" })
        {
            var pending = service.RedeemPinAsync(name, "1234-5678", Ip);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.False(pending.IsCompleted, name + " answered before the floor");
            _time.Advance(TimeSpan.FromMilliseconds(300));
            Assert.Null(await pending);
        }
    }

    [Fact]
    public async Task Request_ByConfirmedAddress_SendsTheSameRecoveryEmailAsByName()
    {
        await Confirm(_bob);
        _config.EmailLogin = false;

        await _service.RequestAsync("bob", Ip);
        await _service.RequestAsync("bob@example.org", Ip);

        var mails = await _transport.WaitForAsync(2);
        Assert.Equal(mails[0].To, mails[1].To);
        Assert.Equal("bob@example.org", mails[1].To);
        Assert.Equal("recovery", mails[1].Template);
        Assert.Equal(mails[0].Subject, mails[1].Subject);
        Assert.Contains("bob", mails[1].Text, StringComparison.Ordinal);
        Assert.Matches(@"\b\d{4}-\d{4}\b", mails[1].Text);
        Assert.Contains("https://jf.example/MailConcierge/Reset?t=", mails[1].Text, StringComparison.Ordinal);
        Assert.Equal(1, await _store.ReadAsync(d => d.Tokens.Count(t => t.Purpose == TokenPurpose.ResetPin)));
    }

    [Theory]
    [InlineData("BOB@Example.ORG")]
    [InlineData("  bob@example.org  ")]
    public async Task Request_ByAddressInOtherCaseOrWithSpaces_Sends(string typed)
    {
        await Confirm(_bob);

        await _service.RequestAsync(typed, Ip);

        var mail = Assert.Single(await _transport.WaitForAsync(1));
        Assert.Equal("bob@example.org", mail.To);
    }

    [Fact]
    public async Task Request_ByUnconfirmedUnknownOrSharedAddress_SendsNothingAndWritesNoToken()
    {
        var alice = Make("alice");
        var carol = Make("carol");
        _users.Setup(u => u.GetUserById(alice.Id)).Returns(alice);
        _users.Setup(u => u.GetUserById(carol.Id)).Returns(carol);
        await _store.WriteAsync(d =>
        {
            d.Emails.Add(new UserEmail(_bob.Id, "bob@example.org", null, "en", null));
            d.Emails.Add(new UserEmail(alice.Id, "shared@example.org", null, "en", _time.GetUtcNow().UtcDateTime));
            d.Emails.Add(new UserEmail(carol.Id, "shared@example.org", null, "en", _time.GetUtcNow().UtcDateTime));
        });

        foreach (var typed in new[] { "bob@example.org", "nobody@example.org", "shared@example.org" })
        {
            await _service.RequestAsync(typed, Ip);
        }

        await _transport.ExpectNoneAsync();
        Assert.Equal(0, await _store.ReadAsync(d => d.Tokens.Count));
    }

    [Fact]
    public async Task Request_UserNameEqualToTypedTextWinsOverAnotherAccountsAddress()
    {
        var odd = Make("bob@example.org");
        _users.Setup(u => u.GetUserByName("bob@example.org")).Returns(odd);
        _users.Setup(u => u.GetUserById(odd.Id)).Returns(odd);
        await Confirm(_bob);
        await _store.WriteAsync(d => d.Emails.Add(new UserEmail(odd.Id, "odd@example.org", null, "en", _time.GetUtcNow().UtcDateTime)));

        await _service.RequestAsync("bob@example.org", Ip);

        var mail = Assert.Single(await _transport.WaitForAsync(1));
        Assert.Equal("odd@example.org", mail.To);
    }

    [Fact]
    public async Task Request_ByAddress_DoesNotDependOnEmailLogin_NorOnPasswordOrFailureCounter()
    {
        _config.EmailLogin = false;
        _bob.Password = null;
        _bob.InvalidLoginAttemptCount = 9;
        await Confirm(_bob);

        await _service.RequestAsync("bob@example.org", Ip);

        Assert.Single(await _transport.WaitForAsync(1));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("disabled")]
    [InlineData("provider")]
    public async Task Request_ByAddress_OfAnIneligibleAccount_SendsNothing(string kind)
    {
        switch (kind)
        {
            case "admin":
                _bob.SetPermission(PermissionKind.IsAdministrator, true);
                break;
            case "disabled":
                _bob.SetPermission(PermissionKind.IsDisabled, true);
                break;
            default:
                _bob.AuthenticationProviderId = "Other.Provider";
                break;
        }

        await Confirm(_bob);

        await _service.RequestAsync("bob@example.org", Ip);

        await _transport.ExpectNoneAsync();
        Assert.Equal(0, await _store.ReadAsync(d => d.Tokens.Count));
    }

    [Fact]
    public async Task Request_AccountLimitIsSharedBetweenNameAndAddress()
    {
        await Confirm(_bob);

        await _service.RequestAsync("bob", Ip);
        await _service.RequestAsync("bob@example.org", Ip);
        await _service.RequestAsync("bob", Ip);
        await _service.RequestAsync("bob@example.org", Ip);
        await _service.RequestAsync("bob", Ip);

        Assert.Equal(3, (await _transport.WaitForAsync(3)).Count);
        await _transport.ExpectNoneAsync(3);
    }

    [Fact]
    public async Task RedeemPin_ByAddress_WithTheRightPin_GivesASession()
    {
        await Confirm(_bob);
        await _service.RequestAsync("bob", Ip);
        var pin = PinOf((await _transport.WaitForAsync(1))[0]);

        Assert.NotNull(await _service.RedeemPinAsync("  Bob@example.org ", pin, Ip));
    }

    [Fact]
    public async Task RedeemPin_ByNameAndByAddress_ExhaustTheSamePin()
    {
        await Confirm(_bob);
        await _service.RequestAsync("bob", Ip);
        var pin = PinOf((await _transport.WaitForAsync(1))[0]);
        var wrong = pin == "0000-0000" ? "1111-1111" : "0000-0000";

        Assert.Null(await _service.RedeemPinAsync("bob", wrong, Ip));
        Assert.Null(await _service.RedeemPinAsync("bob@example.org", wrong, Ip));
        Assert.Null(await _service.RedeemPinAsync("bob", wrong, Ip));
        Assert.Null(await _service.RedeemPinAsync("bob@example.org", wrong, Ip));
        Assert.Equal(1, await _store.ReadAsync(d => d.Tokens.Count(t => t.Purpose == TokenPurpose.ResetPin)));
        Assert.Null(await _service.RedeemPinAsync("bob@example.org", wrong, Ip));

        Assert.Equal(0, await _store.ReadAsync(d => d.Tokens.Count(t => t.Purpose == TokenPurpose.ResetPin)));
        Assert.Null(await _service.RedeemPinAsync("bob", pin, Ip));
    }

    [Fact]
    public async Task RedeemPin_ByUnknownOrSharedAddress_ReturnsNull()
    {
        var alice = Make("alice");
        _users.Setup(u => u.GetUserById(alice.Id)).Returns(alice);
        await Confirm(_bob);
        await _service.RequestAsync("bob", Ip);
        var pin = PinOf((await _transport.WaitForAsync(1))[0]);
        await _store.WriteAsync(d => d.Emails.Add(new UserEmail(alice.Id, "bob@example.org", null, "en", _time.GetUtcNow().UtcDateTime)));

        Assert.Null(await _service.RedeemPinAsync("bob@example.org", pin, Ip));
        Assert.Null(await _service.RedeemPinAsync("nobody@example.org", pin, Ip));
        Assert.NotNull(await _service.RedeemPinAsync("bob", pin, Ip));
    }

    [Fact]
    public async Task RedeemPin_ByAddress_TakesAtLeastTheResponseFloor_WhateverTheOutcome()
    {
        var alice = Make("alice");
        await Confirm(_bob);
        await _store.WriteAsync(d => d.Emails.Add(new UserEmail(alice.Id, "bob@example.org", null, "en", _time.GetUtcNow().UtcDateTime)));
        await Confirm(Make("dave"));
        var service = ServiceWithFloor(TimeSpan.FromMilliseconds(300));

        foreach (var typed in new[] { "nobody@example.org", "bob@example.org", "dave@example.org" })
        {
            var pending = service.RedeemPinAsync(typed, "1234-5678", Ip);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.False(pending.IsCompleted, typed + " answered before the floor");
            _time.Advance(TimeSpan.FromMilliseconds(300));
            Assert.Null(await pending);
        }
    }

    [Fact]
    public async Task AddressLookupFailure_SendsNothing_ReturnsNull_AndLogsOnlyTheExceptionType()
    {
        _users.Setup(u => u.GetUserByName("boom@example.org")).Throws(new InvalidOperationException("secret text boom@example.org"));
        await Confirm(_bob);

        await _service.RequestAsync("boom@example.org", Ip);
        var session = await _service.RedeemPinAsync("boom@example.org", "1234-5678", Ip);

        Assert.Null(session);
        await _transport.ExpectNoneAsync();
        Assert.NotEmpty(_logger.Entries);
        Assert.Contains(_logger.Entries, e => e.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain(_logger.Entries, e => e.Contains("boom@example.org", StringComparison.OrdinalIgnoreCase) || e.Contains("secret text", StringComparison.Ordinal));
    }

    private RecoveryService ServiceWithFloor(TimeSpan floor)
        => new(
            _store, _tokens, new RateLimiter(_time), _queue, new ServiceGate(() => _config, _guard), _users.Object, _resolver, new SessionCloser(_devices.Object, _sessions.Object, _time, NullLogger<SessionCloser>.Instance),
            Mock.Of<IServerApplicationHost>(), () => _config, _time, NullLogger<RecoveryService>.Instance, floor);

    private static User Make(string name)
        => new(name, AccountPolicy.DefaultAuthProviderId, "reset") { Id = Guid.NewGuid() };

    private static string PinOf(EmailMessage m) => Regex.Match(m.Text, @"\d{4}-\d{4}").Value;

    private static string LinkOf(EmailMessage m) => Regex.Match(m.Text, @"[?]t=([\w-]+)").Groups[1].Value;

    private void ReplaceEmail(User user, string address, bool confirmed)
        => _store.WriteAsync(d =>
        {
            d.Emails.RemoveAll(e => e.UserId == user.Id);
            d.Emails.Add(new UserEmail(user.Id, address, null, "en", confirmed ? _time.GetUtcNow().UtcDateTime : null));
        }).GetAwaiter().GetResult();

    // The request reads the clock once per rate-limit check (IP, account) and once more just before storing the tokens:
    // running the hook on the third reading lands it between the first e-mail read and the store write.
    private RecoveryService ServiceWithHookAfterAccountLimit(Action hook)
    {
        var time = new HookedTimeProvider(_time.GetUtcNow(), 3, hook);
        var limiter = new RateLimiter(time);
        return new RecoveryService(
            _store, _tokens, limiter, _queue, new ServiceGate(() => _config, _guard), _users.Object, _resolver, new SessionCloser(_devices.Object, _sessions.Object, time, NullLogger<SessionCloser>.Instance),
            Mock.Of<IServerApplicationHost>(), () => _config, time, NullLogger<RecoveryService>.Instance, TimeSpan.Zero);
    }

    private sealed class HookedTimeProvider : FakeTimeProvider
    {
        private readonly int _fireOn;
        private readonly Action _hook;
        private int _calls;

        public HookedTimeProvider(DateTimeOffset start, int fireOn, Action hook)
            : base(start)
        {
            _fireOn = fireOn;
            _hook = hook;
        }

        public override DateTimeOffset GetUtcNow()
        {
            var now = base.GetUtcNow();
            if (++_calls == _fireOn)
            {
                _hook();
            }

            return now;
        }
    }

    private Task Confirm(User user)
        => _store.WriteAsync(d => d.Emails.Add(new UserEmail(user.Id, user.Username + "@example.org", null, "en", _time.GetUtcNow().UtcDateTime)));

    private sealed class TestLogger : ILogger<RecoveryService>
    {
        private readonly List<string> _entries = new();

        public IReadOnlyList<string> Entries
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
                _entries.Add(formatter(state, exception) + " " + exception);
            }
        }
    }

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
