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
    private readonly User _bob = new("bob", AccountPolicy.DefaultAuthProviderId, "reset") { Id = Guid.NewGuid() };

    public PublicControllerTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new MailConciergeStore(_dir);
        _tokens = new TokenService(_dir);
        var limiter = new RateLimiter(_time);
        var transport = new Mock<IEmailTransport>();
        var queue = new EmailQueue(() => _config, transport.Object, limiter, NullLogger<EmailQueue>.Instance, _time, new[] { TimeSpan.Zero });
        var serverConfig = new Mock<IServerConfigurationManager>();
        serverConfig.Setup(c => c.GetConfiguration("network")).Returns(new NetworkConfiguration { KnownProxies = Array.Empty<string>(), BaseUrl = "/jf" });
        var guard = new ProxyGuard(serverConfig.Object);
        _host.SetupGet(h => h.FriendlyName).Returns("My Jellyfin");
        var closer = new SessionCloser(Mock.Of<IDeviceManager>(), Mock.Of<ISessionManager>(), _time, NullLogger<SessionCloser>.Instance);
        _recovery = new RecoveryService(_store, _tokens, limiter, queue, guard, _users.Object, closer, _host.Object, () => _config, _time, NullLogger<RecoveryService>.Instance, TimeSpan.Zero);
        _email = new EmailService(_store, _tokens, limiter, queue, _users.Object, new SessionCloser(Mock.Of<IDeviceManager>(), Mock.Of<ISessionManager>(), _time, NullLogger<SessionCloser>.Instance), new NullSeerrSync(), _host.Object, () => _config, _time, NullLogger<EmailService>.Instance);
        _invitations = new InvitationService(_store, _tokens, limiter, queue, _users.Object, _email, _host.Object, () => _config, _time, NullLogger<InvitationService>.Instance);
        _renderer = new PageRenderer(_host.Object, serverConfig.Object, () => _config);
        _controller = new PublicController(_recovery, _email, _invitations, _renderer, guard)
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
