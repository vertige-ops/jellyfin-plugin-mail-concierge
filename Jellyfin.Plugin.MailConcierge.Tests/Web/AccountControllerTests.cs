using System;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MailConcierge.Accounts;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Data;
using Jellyfin.Plugin.MailConcierge.Email;
using Jellyfin.Plugin.MailConcierge.Security;
using Jellyfin.Plugin.MailConcierge.Seerr;
using Jellyfin.Plugin.MailConcierge.Tests.Security;
using Jellyfin.Plugin.MailConcierge.Web;
using MediaBrowser.Controller;
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

public sealed class AccountControllerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mailconcierge-account-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private readonly PluginConfiguration _config = new() { PublicUrl = "https://jf.example/", SmtpHost = "smtp.example", FromAddress = "jf@example.com" };
    private readonly Mock<IUserManager> _users = new();
    private readonly ProxyGuard _guard = Guards.Quiet();
    private readonly MailConciergeStore _store;
    private readonly EmailQueue _queue;
    private readonly AccountController _controller;
    private readonly User _bob = new("bob", AccountPolicy.DefaultAuthProviderId, "reset") { Id = Guid.NewGuid() };

    public AccountControllerTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new MailConciergeStore(_dir);
        var limiter = new RateLimiter(_time);
        _queue = new EmailQueue(() => _config, Mock.Of<IEmailTransport>(), limiter, NullLogger<EmailQueue>.Instance, _time);
        var closer = new SessionCloser(Mock.Of<IDeviceManager>(), Mock.Of<ISessionManager>(), _time, NullLogger<SessionCloser>.Instance);
        var email = new EmailService(_store, new TokenService(_dir), limiter, _queue, _users.Object, closer, new NullSeerrSync(), Mock.Of<IServerApplicationHost>(), () => _config, new ServiceGate(() => _config, _guard), _time, NullLogger<EmailService>.Instance);
        _controller = new AccountController(email, null!, _users.Object, () => _config)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        _controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", _bob.Id.ToString("N")) }, "test"));
        _users.Setup(u => u.GetUserById(_bob.Id)).Returns(_bob);
    }

    public void Dispose()
    {
        _queue.Dispose();
        _store.Dispose();
        Directory.Delete(_dir, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_ChangeMine_Returns503Closed(bool guardTripped)
    {
        if (guardTripped)
        {
            Guards.Trip(_guard);
        }
        else
        {
            _config.Enabled = false;
        }

        var result = Assert.IsType<ContentResult>(await _controller.ChangeMine(new MeEmailRequest("new@example.org", "right")));

        Assert.Equal(503, result.StatusCode);
        Assert.Equal("closed", JsonDocument.Parse(result.Content!).RootElement.GetProperty("code").GetString());
        _users.Verify(u => u.AuthenticateUser(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }
}
