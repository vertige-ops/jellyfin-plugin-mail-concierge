using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MailConcierge.Accounts;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Data;
using Jellyfin.Plugin.MailConcierge.Email;
using Jellyfin.Plugin.MailConcierge.Invitations;
using Jellyfin.Plugin.MailConcierge.Security;
using Jellyfin.Plugin.MailConcierge.Seerr;
using Jellyfin.Plugin.MailConcierge.Tests.Security;
using Jellyfin.Plugin.MailConcierge.Web;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Web;

public sealed class PublicControllerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mailconcierge-public-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private readonly PluginConfiguration _config = new()
    {
        Enabled = true,
        PublicUrl = "https://jf.example/",
        SmtpHost = "smtp.example",
        FromAddress = "jf@example.com",
        DefaultLanguage = "en",
    };

    private readonly Mock<IUserManager> _users = new();
    private readonly Mock<IServerApplicationHost> _host = new();
    private readonly MailConciergeStore _store;
    private readonly TokenService _tokens;
    private readonly RecoveryService _recovery;
    private readonly EmailService _email;
    private readonly InvitationService _invitations;
    private readonly PageRenderer _renderer;
    private readonly PublicController _controller;
    private readonly ProxyGuard _guard;
    private readonly RateLimiter _limiter;
    private readonly User _bob = new("bob", AccountPolicy.DefaultAuthProviderId, "reset") { Id = Guid.NewGuid() };

    public PublicControllerTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new MailConciergeStore(_dir);
        _tokens = new TokenService(_dir);
        var limiter = new RateLimiter(_time);
        _limiter = limiter;
        var transport = new Mock<IEmailTransport>();
        var queue = new EmailQueue(() => _config, transport.Object, NullLogger<EmailQueue>.Instance, _time, new[] { TimeSpan.Zero });
        var serverConfig = new Mock<IServerConfigurationManager>();
        serverConfig.Setup(c => c.GetConfiguration("network")).Returns(new NetworkConfiguration { KnownProxies = Array.Empty<string>(), BaseUrl = "/jf" });
        var guard = new ProxyGuard(serverConfig.Object);
        _guard = guard;
        var gate = new ServiceGate(() => _config, guard);
        _host.SetupGet(h => h.FriendlyName).Returns("My Jellyfin");
        var closer = new SessionCloser(Mock.Of<IDeviceManager>(), Mock.Of<ISessionManager>(), _time, NullLogger<SessionCloser>.Instance);
        _email = new EmailService(_store, _tokens, limiter, queue, _users.Object, new SessionCloser(Mock.Of<IDeviceManager>(), Mock.Of<ISessionManager>(), _time, NullLogger<SessionCloser>.Instance), new NullSeerrSync(), _host.Object, () => _config, gate, _time, NullLogger<EmailService>.Instance);
        _recovery = new RecoveryService(_store, _tokens, limiter, queue, gate, _users.Object, new EmailLoginResolver(_users.Object, _email, () => _config), closer, _host.Object, () => _config, _time, NullLogger<RecoveryService>.Instance, TimeSpan.Zero);
        _invitations = new InvitationService(_store, _tokens, limiter, queue, _users.Object, Mock.Of<ISessionManager>(), _email, _host.Object, () => _config, gate, _time, NullLogger<InvitationService>.Instance);
        _renderer = new PageRenderer(_host.Object, serverConfig.Object, () => _config);
        _controller = new PublicController(_recovery, _email, _invitations, _renderer, guard, gate, limiter)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        _controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        _users.Setup(u => u.GetUserByName("bob")).Returns(_bob);
        _users.Setup(u => u.GetUserById(_bob.Id)).Returns(_bob);
        _users.Setup(u => u.ChangePassword(It.IsAny<Guid>(), It.IsAny<string>())).Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task AcceptPage_Get_DoesNotConsume_AndPrefillsEscaped()
    {
        var (_, link, _) = await _invitations.CreateAsync("al'ice", "a@example.org", Array.Empty<string>(), null, "en");
        var token = link[(link.IndexOf("t=", StringComparison.Ordinal) + 2)..];

        var first = Assert.IsType<PageResult>(await _controller.AcceptPage(token));
        Assert.IsType<PageResult>(await _controller.AcceptPage(token));

        Assert.Contains("value=\"al&#39;ice\"", first.Content, StringComparison.Ordinal);
        Assert.Contains("\"page\":\"accept\"", first.Content, StringComparison.Ordinal);
        Assert.NotNull(await _invitations.PeekAsync(token));
    }

    [Fact]
    public async Task AcceptPage_UsesInvitationLanguage()
    {
        var (_, link, _) = await _invitations.CreateAsync("alice", null, Array.Empty<string>(), null, "fr");
        var token = link[(link.IndexOf("t=", StringComparison.Ordinal) + 2)..];
        _controller.Request.Headers.AcceptLanguage = "en";

        var page = Assert.IsType<PageResult>(await _controller.AcceptPage(token));

        Assert.Contains("\"lang\":\"fr\"", page.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptPage_UnknownToken_ShowsGenericMessage()
    {
        var page = Assert.IsType<PageResult>(await _controller.AcceptPage("nope"));

        Assert.DoesNotContain("accept-form", page.Content, StringComparison.Ordinal);
        Assert.Contains("no longer works", page.Content, StringComparison.Ordinal);
        Assert.Contains("Ask the person who invited you", page.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accept_ShortPassword_IsRefused_BeforeConsuming()
    {
        var (_, link, _) = await _invitations.CreateAsync("alice", null, Array.Empty<string>(), null, "en");
        var token = link[(link.IndexOf("t=", StringComparison.Ordinal) + 2)..];

        var result = Assert.IsType<ContentResult>(await _controller.Accept(new AcceptRequest(token, "alice", "a@example.org", "short", "en")));

        Assert.Equal(400, result.StatusCode);
        Assert.NotNull(await _invitations.PeekAsync(token));
    }

    [Fact]
    public async Task Accept_Errors_UseTheInvitationLanguage_WhenSupported()
    {
        _controller.Request.Headers.AcceptLanguage = "fr";

        var result = Assert.IsType<ContentResult>(await _controller.Accept(new AcceptRequest("x", "alice", "a@example.org", "short", "en")));

        Assert.Contains(EmailTemplates.GetString("en", "password.tooShort"), result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetPage_Get_DoesNotConsumeToken()
    {
        var link = await SeedLink();

        Assert.IsType<PageResult>(await _controller.Reset(link, null));
        Assert.IsType<PageResult>(await _controller.Reset(link, null));

        var result = Assert.IsType<ContentResult>(await _controller.ResetLink(new LinkRequest(link)));
        Assert.Equal(200, result.StatusCode);
        Assert.False(string.IsNullOrEmpty(JsonDocument.Parse(result.Content!).RootElement.GetProperty("session").GetString()));
    }

    [Fact]
    public async Task ResetPage_HasCspWithNonce_AndNoReferrer()
    {
        var page = Assert.IsType<PageResult>(await _controller.Reset(await SeedLink(), null));

        var headers = _controller.Response.Headers;
        var csp = headers["Content-Security-Policy"].ToString();
        Assert.Equal(
            "default-src 'self'; script-src 'self' 'nonce-" + page.Nonce + "'; style-src 'self' 'nonce-" + page.Nonce
            + "'; img-src 'self' data:; base-uri 'none'; form-action 'self'; frame-ancestors 'none'",
            csp);
        Assert.Equal("no-referrer", headers["Referrer-Policy"]);
        Assert.Equal("nosniff", headers["X-Content-Type-Options"]);
        Assert.Equal("no-store", headers["Cache-Control"]);
        Assert.Contains("nonce=\"" + page.Nonce + "\"", page.Content, StringComparison.Ordinal);
        Assert.Contains("href=\"/jf/MailConcierge/assets/pages.css\"", page.Content, StringComparison.Ordinal);
        Assert.Contains("src=\"/jf/MailConcierge/assets/form-logo.png\"", page.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", page.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAndCancel_Post_SucceedWithValidToken_AndFailGenericallyOtherwise()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var confirm = _tokens.NewToken();
        var cancel = _tokens.NewToken();
        _users.Setup(u => u.AuthenticateUser("bob", "right", "203.0.113.7", false)).ReturnsAsync(_bob);
        _users.Setup(u => u.AuthenticateUser("bob", "wrong", "203.0.113.7", false)).ReturnsAsync((User?)null);
        await _store.WriteAsync(d =>
        {
            d.Emails.Add(new UserEmail(_bob.Id, null, "new@example.org", "en", null));
            d.Tokens.Add(new TokenRecord(_tokens.Hash(confirm), TokenPurpose.ConfirmEmail, _bob.Id, null, "new@example.org", now.AddHours(1), 0));
        });

        Assert.IsType<NoContentResult>(await _controller.Confirm(new LinkRequest(confirm)));
        var again = Assert.IsType<ContentResult>(await _controller.Confirm(new LinkRequest(confirm)));
        await _store.WriteAsync(d => d.Tokens.Add(new TokenRecord(_tokens.Hash(cancel), TokenPurpose.CancelEmailChange, _bob.Id, null, "old@example.org", now.AddHours(1), 0)));
        var wrong = Assert.IsType<ContentResult>(await _controller.Cancel(new CancelRequest(cancel, "wrong")));
        Assert.Equal(403, wrong.StatusCode);
        var ok = await _controller.Cancel(new CancelRequest(cancel, "right"));
        Assert.True(ok is NoContentResult, (ok as ContentResult)?.Content);
        var bad = Assert.IsType<ContentResult>(await _controller.Cancel(new CancelRequest("nope", "right")));

        Assert.Equal(400, again.StatusCode);
        Assert.Equal(again.Content, bad.Content);
        Assert.Equal("old@example.org", (await _email.GetAsync(_bob.Id))!.Email);
    }

    [Fact]
    public async Task ResetPin_Endpoint_ReturnsGenericErrorText()
    {
        var pin = _tokens.NewPin();
        await _store.WriteAsync(d =>
        {
            d.Emails.Add(new UserEmail(_bob.Id, "bob@example.org", null, "en", _time.GetUtcNow().UtcDateTime));
            d.Tokens.Add(new TokenRecord(_tokens.Hash(pin), TokenPurpose.ResetPin, _bob.Id, null, null, _time.GetUtcNow().UtcDateTime.AddMinutes(30), 0));
        });
        var wrong = pin == "0000-0000" ? "1111-1111" : "0000-0000";
        var wrongUser = Assert.IsType<ContentResult>(await _controller.ResetPin(new PinRequest("ghost", pin)));
        var wrongPin = Assert.IsType<ContentResult>(await _controller.ResetPin(new PinRequest("bob", wrong)));

        Assert.Equal(400, wrongUser.StatusCode);
        Assert.Equal(400, wrongPin.StatusCode);
        Assert.Equal(wrongUser.Content, wrongPin.Content);
        Assert.DoesNotContain(wrong, wrongPin.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinkAndComplete_Failures_UseTheSameGenericText()
    {
        var pin = Assert.IsType<ContentResult>(await _controller.ResetPin(new PinRequest("ghost", "1234-5678")));
        var link = Assert.IsType<ContentResult>(await _controller.ResetLink(new LinkRequest("nope")));
        var complete = Assert.IsType<ContentResult>(await _controller.ResetComplete(new CompleteRequest("nope", "long enough password")));

        Assert.Equal(pin.Content, link.Content);
        Assert.Equal(pin.Content, complete.Content);
    }

    [Fact]
    public async Task Complete_RejectsShortPassword()
    {
        var link = await SeedLink();
        var session = JsonDocument.Parse(Assert.IsType<ContentResult>(await _controller.ResetLink(new LinkRequest(link))).Content!)
            .RootElement.GetProperty("session").GetString();

        var short1 = Assert.IsType<ContentResult>(await _controller.ResetComplete(new CompleteRequest(session, "short")));

        Assert.Equal(400, short1.StatusCode);
        Assert.Contains("at least 8", short1.Content!, StringComparison.Ordinal);
        _users.Verify(u => u.ChangePassword(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);

        _controller.Request.Headers.AcceptLanguage = "fr-FR,fr;q=0.9";
        var shortFr = Assert.IsType<ContentResult>(await _controller.ResetComplete(new CompleteRequest(session, "short")));
        Assert.Contains("caractères", JsonDocument.Parse(shortFr.Content!).RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);

        // The session was not burned by the refused password.
        Assert.IsType<NoContentResult>(await _controller.ResetComplete(new CompleteRequest(session, "long enough password")));
        _users.Verify(u => u.ChangePassword(_bob.Id, "long enough password"), Times.Once);
    }

    [Theory]
    [InlineData("../Plugin.cs")]
    [InlineData("..%2FPlugin.cs")]
    [InlineData("layout.html")]
    [InlineData("")]
    public void Assets_RejectsPathTraversal(string file)
    {
        Assert.IsType<NotFoundResult>(_controller.Assets(file));
    }

    [Theory]
    [InlineData("pages.css", "text/css")]
    [InlineData("pages.js", "text/javascript")]
    public void Assets_ServesAllowListedFiles(string file, string type)
    {
        var result = Assert.IsType<ContentResult>(_controller.Assets(file));

        Assert.StartsWith(type, result.ContentType, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(result.Content));
    }

    [Fact]
    public void Assets_ServesTheOfficialLogoAsPng()
    {
        var result = Assert.IsType<FileContentResult>(_controller.Assets("logo.png"));

        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, result.FileContents[..4]);
        Assert.Equal("nosniff", _controller.Response.Headers["X-Content-Type-Options"]);
    }

    [Fact]
    public void Assets_ServesTheFormLogoAsPng()
    {
        var result = Assert.IsType<FileContentResult>(_controller.Assets("form-logo.png"));

        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, result.FileContents[..4]);
        Assert.Equal("nosniff", _controller.Response.Headers["X-Content-Type-Options"]);
    }

    [Fact]
    public async Task Page_EscapesServerName()
    {
        _host.SetupGet(h => h.FriendlyName).Returns("<script>alert(1)</script>");

        var page = Assert.IsType<PageResult>(await _controller.Reset(await SeedLink(), null));

        Assert.DoesNotContain("<script>alert(1)", page.Content, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", page.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fr-FR,fr;q=0.9,en;q=0.8", "fr")]
    [InlineData("en-US,en;q=0.9,fr;q=0.8", "en")]
    [InlineData("ja,fr;q=0.5", "fr")]
    [InlineData("ja,it", "en")]
    [InlineData("", "en")]
    [InlineData("fr;q=0,en;q=0.1", "en")]
    public void PickLanguage_UsesSupportedLanguagesOnly(string header, string expected)
    {
        Assert.Equal(expected, _renderer.PickLanguage(header));
    }

    [Fact]
    public void PickLanguage_UnsupportedBrowserLanguage_ReturnsEnglishEvenWhenDefaultIsFrench()
    {
        _config.DefaultLanguage = "fr";

        Assert.Equal("en", _renderer.PickLanguage("ja-JP"));
    }

    [Fact]
    public void PickLanguage_FrenchAccepted_ReturnsFr()
    {
        _config.DefaultLanguage = "en";

        Assert.Equal("fr", _renderer.PickLanguage("fr-FR,fr;q=0.9"));
    }

    [Fact]
    public async Task ResetPage_UsesTheDefaultLanguage_NotTheStoredOneNorTheBrowser()
    {
        var link = await SeedLink("en");
        _config.DefaultLanguage = "fr";
        _controller.Request.Headers.AcceptLanguage = "ja";

        var page = Assert.IsType<PageResult>(await _controller.Reset(link, null));

        Assert.Contains("\"lang\":\"fr\"", page.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetPage_BlankStoredLanguage_UsesTheDefaultLanguage()
    {
        var link = await SeedLink(string.Empty);
        _config.DefaultLanguage = "fr";
        _controller.Request.Headers.AcceptLanguage = "ja";

        var page = Assert.IsType<PageResult>(await _controller.Reset(link, null));

        Assert.Contains("\"lang\":\"fr\"", page.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAndCancelPages_UseTheDefaultLanguage_NotTheStoredOne()
    {
        _config.DefaultLanguage = "fr";
        var now = _time.GetUtcNow().UtcDateTime;
        var confirm = _tokens.NewToken();
        var cancel = _tokens.NewToken();
        await _store.WriteAsync(d =>
        {
            d.Emails.Add(new UserEmail(_bob.Id, "bob@example.org", "new@example.org", "en", now));
            d.Tokens.Add(new TokenRecord(_tokens.Hash(confirm), TokenPurpose.ConfirmEmail, _bob.Id, null, "new@example.org", now.AddHours(1), 0));
            d.Tokens.Add(new TokenRecord(_tokens.Hash(cancel), TokenPurpose.CancelEmailChange, _bob.Id, null, "old@example.org", now.AddHours(1), 0));
        });
        _controller.Request.Headers.AcceptLanguage = "ja";

        var confirmPage = Assert.IsType<PageResult>(await _controller.ConfirmPage(confirm));
        var cancelPage = Assert.IsType<PageResult>(await _controller.CancelPage(cancel));

        Assert.Contains("Confirmer cette adresse", confirmPage.Content, StringComparison.Ordinal);
        Assert.Contains("Annuler le changement", cancelPage.Content, StringComparison.Ordinal);
        Assert.Contains("name=\"password\"", cancelPage.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptPage_KeepsTheInvitationLanguage_WhateverTheBrowserSays()
    {
        var (_, link, _) = await _invitations.CreateAsync("alice", null, Array.Empty<string>(), null, "fr");
        var token = link[(link.IndexOf("t=", StringComparison.Ordinal) + 2)..];
        _controller.Request.Headers.AcceptLanguage = "ja";

        var page = Assert.IsType<PageResult>(await _controller.AcceptPage(token));

        Assert.Contains("\"lang\":\"fr\"", page.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pages_AreLocalized_AndSessionLinkSkipsNothingServerSide()
    {
        _controller.Request.Headers.AcceptLanguage = "fr";

        var reset = Assert.IsType<PageResult>(await _controller.Reset(null, "session"));
        var confirm = Assert.IsType<PageResult>(await _controller.ConfirmPage("abc"));
        var cancel = Assert.IsType<PageResult>(await _controller.CancelPage("abc"));
        var missing = Assert.IsType<PageResult>(await _controller.Reset(null, null));

        Assert.Contains("lang=\"fr\"", reset.Content, StringComparison.Ordinal);
        Assert.Contains("Changer mon mot de passe", reset.Content, StringComparison.Ordinal);
        Assert.Contains("Confirmer cette adresse", confirm.Content, StringComparison.Ordinal);
        Assert.Contains("Annuler le changement", cancel.Content, StringComparison.Ordinal);
        Assert.Contains("Ce lien ne fonctionne plus", missing.Content, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"\{\{"), reset.Content!);
    }

    [Fact]
    public async Task ResetPage_UsedUnknownOrExpiredLink_ShowsTheSameInvalidPage()
    {
        var used = await SeedLink();
        Assert.IsType<ContentResult>(await _controller.ResetLink(new LinkRequest(used)));
        var expired = await SeedLink();
        _time.Advance(TimeSpan.FromMinutes(31));
        var fresh = await SeedLink();

        var pages = new System.Collections.Generic.List<string>();
        foreach (var t in new[] { used, "nope", expired })
        {
            var page = Assert.IsType<PageResult>(await _controller.Reset(t, null));
            Assert.Contains("no longer works", page.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("reset-form", page.Content, StringComparison.Ordinal);
            pages.Add(Regex.Replace(page.Content!, "nonce=\"[^\"]*\"|'nonce-[^']*'", string.Empty));
        }

        Assert.Single(pages.Distinct());
        var valid = Assert.IsType<PageResult>(await _controller.Reset(fresh, null));
        Assert.Contains("reset-form", valid.Content, StringComparison.Ordinal);
        Assert.IsType<ContentResult>(await _controller.ResetLink(new LinkRequest(fresh)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_GetConfirm_ShowsMessagePage(bool guardTripped)
    {
        var (confirm, _) = await SeedEmailChangeTokens();
        Close(guardTripped);

        var page = Assert.IsType<PageResult>(await _controller.ConfirmPage(confirm));

        AssertMessagePage(page);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_GetCancel_ShowsMessagePage(bool guardTripped)
    {
        var (_, cancel) = await SeedEmailChangeTokens();
        Close(guardTripped);

        var page = Assert.IsType<PageResult>(await _controller.CancelPage(cancel));

        AssertMessagePage(page);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_GetReset_ShowsMessagePage(bool guardTripped)
    {
        var link = await SeedLink();
        Close(guardTripped);

        AssertMessagePage(Assert.IsType<PageResult>(await _controller.Reset(link, null)));
        AssertMessagePage(Assert.IsType<PageResult>(await _controller.Reset(null, "session")));
    }

    [Fact]
    public async Task PageViews_31stWithinFiveMinutes_Gets429()
    {
        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(200, Assert.IsType<PageResult>(await _controller.ConfirmPage("x")).StatusCode);
        }

        var page = Assert.IsType<PageResult>(await _controller.ConfirmPage("x"));

        Assert.Equal(429, page.StatusCode);
        Assert.Contains("Too many pages were opened from your network", page.Content, StringComparison.Ordinal);
        Assert.Contains("Too many requests", page.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("<form", page.Content, StringComparison.Ordinal);
        Assert.Equal("no-store", _controller.Response.Headers.CacheControl.ToString());
        Assert.True(_controller.Response.Headers.ContainsKey("Content-Security-Policy"));
    }

    [Fact]
    public async Task PageViews_AreSharedAcrossTheFourPages()
    {
        for (var i = 0; i < 30; i++)
        {
            switch (i % 4)
            {
                case 0:
                    await _controller.Reset("x", null);
                    break;
                case 1:
                    await _controller.ConfirmPage("x");
                    break;
                case 2:
                    await _controller.CancelPage("x");
                    break;
                default:
                    await _controller.AcceptPage("x");
                    break;
            }
        }

        Assert.Equal(429, Assert.IsType<PageResult>(await _controller.Reset("x", null)).StatusCode);
        Assert.Equal(429, Assert.IsType<PageResult>(await _controller.ConfirmPage("x")).StatusCode);
        Assert.Equal(429, Assert.IsType<PageResult>(await _controller.CancelPage("x")).StatusCode);
        Assert.Equal(429, Assert.IsType<PageResult>(await _controller.AcceptPage("x")).StatusCode);
    }

    [Fact]
    public async Task PageViews_OtherAddress_IsNotLimited()
    {
        for (var i = 0; i < 31; i++)
        {
            await _controller.ConfirmPage("x");
        }

        _controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.8");

        Assert.Equal(200, Assert.IsType<PageResult>(await _controller.ConfirmPage("x")).StatusCode);
    }

    [Fact]
    public async Task PageViews_ResetAfterFiveMinutes()
    {
        for (var i = 0; i < 31; i++)
        {
            await _controller.ConfirmPage("x");
        }

        _time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        Assert.Equal(200, Assert.IsType<PageResult>(await _controller.ConfirmPage("x")).StatusCode);
    }

    [Fact]
    public async Task PageViews_OverLimit_DoesNotReadTheToken()
    {
        var (_, link, _) = await _invitations.CreateAsync("alice", null, Array.Empty<string>(), null, "en");
        var token = link[(link.IndexOf("t=", StringComparison.Ordinal) + 2)..];
        for (var i = 0; i < 30; i++)
        {
            await _controller.ConfirmPage("x");
        }

        Assert.Equal(429, Assert.IsType<PageResult>(await _controller.AcceptPage(token)).StatusCode);
        for (var i = 0; i < 12; i++)
        {
            Assert.Equal(429, Assert.IsType<PageResult>(await _controller.AcceptPage("wrong")).StatusCode);
        }

        // Had the wrong tokens been read, ten failures would have blocked this address for fifteen minutes.
        _time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var page = Assert.IsType<PageResult>(await _controller.AcceptPage(token));
        Assert.Contains("accept-form", page.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PageViews_AreCountedBeforeTheGate_WhateverTheState(bool guardTripped)
    {
        Close(guardTripped);
        for (var i = 0; i < 30; i++)
        {
            AssertMessagePage(Assert.IsType<PageResult>(await _controller.ConfirmPage("x")));
        }

        var over = Assert.IsType<PageResult>(await _controller.ConfirmPage("x"));

        Assert.Equal(429, over.StatusCode);
        Assert.Contains("Too many pages were opened from your network", over.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Reset")]
    [InlineData("Confirm")]
    [InlineData("Cancel")]
    [InlineData("Accept")]
    public async Task EveryPageView_ObservesTheProxyGuard(string page)
    {
        _controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("100.64.0.9");
        _controller.HttpContext.Request.Headers["X-Forwarded-For"] = "203.0.113.5";
        Assert.False(_guard.Tripped);

        _ = page switch
        {
            "Reset" => await _controller.Reset("t", null),
            "Confirm" => await _controller.ConfirmPage("t"),
            "Cancel" => await _controller.CancelPage("t"),
            _ => await _controller.AcceptPage("t"),
        };

        Assert.True(_guard.Tripped);
    }

    [Theory]
    [InlineData("Reset", "reset")]
    [InlineData("Confirm", "confirm")]
    [InlineData("Cancel", "cancel")]
    [InlineData("Accept", "accept")]
    public async Task Get_WithoutToken_ShowsTheEntryPageOfItsTarget(string route, string target)
    {
        var page = Assert.IsType<PageResult>(route switch
        {
            "Reset" => await _controller.Reset(null, null),
            "Confirm" => await _controller.ConfirmPage(null),
            "Cancel" => await _controller.CancelPage(null),
            _ => await _controller.AcceptPage(null),
        });

        Assert.Contains("\"page\":\"entry\"", page.Content, StringComparison.Ordinal);
        Assert.Contains("\"target\":\"" + target + "\"", page.Content, StringComparison.Ordinal);
        Assert.Contains("Opening your link", page.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("<form", page.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("\"token\"", page.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_LegacyQuery_CarriesTheTokenAndSessionInTheJsonData()
    {
        var link = await SeedLink();
        var reset = Assert.IsType<PageResult>(await _controller.Reset(link, null));
        var session = Assert.IsType<PageResult>(await _controller.Reset(null, "sess-1_A"));
        var (confirm, cancel) = await SeedEmailChangeTokens();
        var confirmPage = Assert.IsType<PageResult>(await _controller.ConfirmPage(confirm));
        var cancelPage = Assert.IsType<PageResult>(await _controller.CancelPage(cancel));

        Assert.Equal(link, DataOf(reset).GetProperty("token").GetString());
        Assert.Equal("sess-1_A", DataOf(session).GetProperty("session").GetString());
        Assert.Equal(confirm, DataOf(confirmPage).GetProperty("token").GetString());
        Assert.Equal(cancel, DataOf(cancelPage).GetProperty("token").GetString());
        Assert.Equal("reset", DataOf(reset).GetProperty("page").GetString());
        Assert.True(DataOf(reset).TryGetProperty("minPasswordLength", out _));
    }

    [Fact]
    public async Task Post_ValidToken_RendersThePage_WithTheTokenOnlyInTheJsonData()
    {
        var link = await SeedLink();

        var page = Assert.IsType<PageResult>(await _controller.Open("reset", link, null));

        Assert.Equal(200, page.StatusCode);
        Assert.Contains("reset-form", page.Content, StringComparison.Ordinal);
        Assert.Equal(link, DataOf(page).GetProperty("token").GetString());
        Assert.Equal(1, page.Content!.Split(link).Length - 1);
        Assert.Equal("no-store", _controller.Response.Headers.CacheControl.ToString());
        Assert.True(_controller.Response.Headers.ContainsKey("Content-Security-Policy"));
        foreach (var header in _controller.Response.Headers)
        {
            Assert.DoesNotContain(link, header.Value.ToString(), StringComparison.Ordinal);
        }

        Assert.False(_controller.Response.Headers.ContainsKey("Location"));
    }

    [Fact]
    public async Task Post_ConfirmCancelAndAccept_CarryTheSecurityHeaders_AndNoTokenInAnyHeader()
    {
        var (confirm, cancel) = await SeedEmailChangeTokens();
        var (_, link, _) = await _invitations.CreateAsync("alice", "a@example.org", Array.Empty<string>(), null, "en");
        var invitation = link[(link.IndexOf("t=", StringComparison.Ordinal) + 2)..];

        foreach (var (page, token) in new[] { ("confirm", confirm), ("cancel", cancel), ("accept", invitation) })
        {
            _controller.ControllerContext.HttpContext.Response.Headers.Clear();
            var result = Assert.IsType<PageResult>(await _controller.Open(page, token, null));

            Assert.Equal(200, result.StatusCode);
            Assert.Equal(token, DataOf(result).GetProperty("token").GetString());
            Assert.Equal("no-store", _controller.Response.Headers.CacheControl.ToString());
            Assert.True(_controller.Response.Headers.ContainsKey("Content-Security-Policy"), page);
            Assert.Equal("no-referrer", _controller.Response.Headers["Referrer-Policy"].ToString());
            Assert.False(_controller.Response.Headers.ContainsKey("Location"), page);
            foreach (var header in _controller.Response.Headers)
            {
                Assert.DoesNotContain(token, header.Value.ToString(), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Post_ResetSession_RendersTheResetPage_WithTheSessionInTheJsonData()
    {
        var page = Assert.IsType<PageResult>(await _controller.Open("reset", null, "abc_DEF-123"));

        Assert.Contains("reset-form", page.Content, StringComparison.Ordinal);
        Assert.Equal("abc_DEF-123", DataOf(page).GetProperty("session").GetString());
    }

    [Fact]
    public async Task Post_ConfirmCancelAndAccept_RenderTheirPages()
    {
        var (confirm, cancel) = await SeedEmailChangeTokens();
        var (_, link, _) = await _invitations.CreateAsync("alice", "a@example.org", Array.Empty<string>(), null, "en");
        var invitation = link[(link.IndexOf("t=", StringComparison.Ordinal) + 2)..];

        var confirmPage = Assert.IsType<PageResult>(await _controller.Open("confirm", confirm, null));
        var cancelPage = Assert.IsType<PageResult>(await _controller.Open("cancel", cancel, null));
        var acceptPage = Assert.IsType<PageResult>(await _controller.Open("accept", invitation, null));

        Assert.Equal("confirm", DataOf(confirmPage).GetProperty("page").GetString());
        Assert.Equal(confirm, DataOf(confirmPage).GetProperty("token").GetString());
        Assert.Equal("cancel", DataOf(cancelPage).GetProperty("page").GetString());
        Assert.Equal(cancel, DataOf(cancelPage).GetProperty("token").GetString());
        Assert.Equal("accept", DataOf(acceptPage).GetProperty("page").GetString());
        Assert.Equal(invitation, DataOf(acceptPage).GetProperty("token").GetString());
        Assert.Contains("accept-form", acceptPage.Content, StringComparison.Ordinal);
        Assert.NotNull(await _invitations.PeekAsync(invitation));
    }

    [Fact]
    public async Task Post_UnknownUsedExpiredOrForeignToken_GivesTheSameInvalidPageAsGet()
    {
        var used = await SeedLink();
        Assert.IsType<ContentResult>(await _controller.ResetLink(new LinkRequest(used)));
        var expired = await SeedLink();
        _time.Advance(TimeSpan.FromMinutes(31));
        var (confirm, _) = await SeedEmailChangeTokens();

        var expected = Normalize(Assert.IsType<PageResult>(await _controller.Reset("nope", null)));
        foreach (var t in new[] { used, "nope", expired, confirm, null, string.Empty })
        {
            var page = Assert.IsType<PageResult>(await _controller.Open("reset", t, null));
            Assert.Equal(expected, Normalize(page));
            Assert.DoesNotContain("reset-form", page.Content, StringComparison.Ordinal);
            Assert.False(DataOf(page).TryGetProperty("token", out _));
        }

        var missing = Normalize(Assert.IsType<PageResult>(await _controller.AcceptPage("nope")));
        Assert.Equal(missing, Normalize(Assert.IsType<PageResult>(await _controller.Open("accept", "nope", null))));
        Assert.Equal(missing, Normalize(Assert.IsType<PageResult>(await _controller.Open("accept", null, null))));
        var (_, cancel) = await SeedEmailChangeTokens();

        // The confirmation and cancellation pages decide nothing about the token (the POST they send does), so a foreign or unknown
        // token gets the same page as a valid one, on POST as on GET: there is no oracle.
        var wrongPurpose = Assert.IsType<PageResult>(await _controller.Open("confirm", cancel, null));
        var viaGet = Assert.IsType<PageResult>(await _controller.ConfirmPage(cancel));
        Assert.Equal(Normalize(viaGet), Normalize(wrongPurpose));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("has space")]
    [InlineData("a.b")]
    public async Task MalformedToken_IsTreatedAsAnInvalidLink_OnGetAndPost(string token)
    {
        var long200 = new string('a', 200);
        foreach (var t in new[] { token, long200 })
        {
            var post = Assert.IsType<PageResult>(await _controller.Open("reset", t, null));
            var get = Assert.IsType<PageResult>(await _controller.Reset(t, null));
            var session = Assert.IsType<PageResult>(await _controller.Open("reset", null, t));
            foreach (var page in new[] { post, get, session })
            {
                Assert.Contains("no longer works", page.Content, StringComparison.Ordinal);
                Assert.DoesNotContain("<script>alert", page.Content, StringComparison.Ordinal);
                Assert.DoesNotContain(t, page.Content, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Post_OverTheRateLimit_GetsTheTooManyPage()
    {
        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(200, Assert.IsType<PageResult>(await _controller.Open("confirm", "x", null)).StatusCode);
        }

        var page = Assert.IsType<PageResult>(await _controller.Open("reset", "x", null));

        Assert.Equal(429, page.StatusCode);
        Assert.Contains("Too many pages were opened from your network", page.Content, StringComparison.Ordinal);
        Assert.Equal(429, Assert.IsType<PageResult>(await _controller.ConfirmPage("x")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_WhileTheServiceIsClosed_ShowsTheMessagePage(bool guardTripped)
    {
        var link = await SeedLink();
        var (confirm, cancel) = await SeedEmailChangeTokens();
        Close(guardTripped);

        AssertMessagePage(Assert.IsType<PageResult>(await _controller.Open("reset", link, null)));
        AssertMessagePage(Assert.IsType<PageResult>(await _controller.Open("reset", null, "session")));
        AssertMessagePage(Assert.IsType<PageResult>(await _controller.Open("confirm", confirm, null)));
        AssertMessagePage(Assert.IsType<PageResult>(await _controller.Open("cancel", cancel, null)));
    }

    [Theory]
    [InlineData("login")]
    [InlineData("")]
    [InlineData("Reset/Link")]
    public async Task Post_UnknownPage_Is404(string page)
    {
        Assert.IsType<NotFoundResult>(await _controller.Open(page, "abc", null));
    }

    [Fact]
    public async Task Post_PageNameIsCaseInsensitive()
    {
        var page = Assert.IsType<PageResult>(await _controller.Open("Reset", await SeedLink(), null));

        Assert.Contains("reset-form", page.Content, StringComparison.Ordinal);
    }

    private static JsonElement DataOf(PageResult page)
    {
        var match = Regex.Match(page.Content!, "id=\"mailconcierge-data\"[^>]*>(.*?)</script>", RegexOptions.Singleline);
        Assert.True(match.Success);
        return JsonDocument.Parse(match.Groups[1].Value).RootElement.Clone();
    }

    private static string Normalize(PageResult page) => Regex.Replace(page.Content!, "nonce=\"[^\"]*\"|'nonce-[^']*'", string.Empty);

    private static void AssertMessagePage(PageResult page)
    {
        Assert.Contains("no longer works", page.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("<form", page.Content, StringComparison.Ordinal);
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
    }

    private async Task<(string Confirm, string Cancel)> SeedEmailChangeTokens()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var confirm = _tokens.NewToken();
        var cancel = _tokens.NewToken();
        await _store.WriteAsync(d =>
        {
            d.Emails.Add(new UserEmail(_bob.Id, "bob@example.org", "new@example.org", "en", now));
            d.Tokens.Add(new TokenRecord(_tokens.Hash(confirm), TokenPurpose.ConfirmEmail, _bob.Id, null, "new@example.org", now.AddHours(1), 0));
            d.Tokens.Add(new TokenRecord(_tokens.Hash(cancel), TokenPurpose.CancelEmailChange, _bob.Id, null, "old@example.org", now.AddHours(1), 0));
        });
        return (confirm, cancel);
    }

    private async Task<string> SeedLink(string language = "en")
    {
        var token = _tokens.NewToken();
        await _store.WriteAsync(d =>
        {
            d.Emails.Add(new UserEmail(_bob.Id, "bob@example.org", null, language, _time.GetUtcNow().UtcDateTime));
            d.Tokens.Add(new TokenRecord(_tokens.Hash(token), TokenPurpose.ResetLink, _bob.Id, null, null, _time.GetUtcNow().UtcDateTime.AddMinutes(30), 0));
        });
        return token;
    }
}
