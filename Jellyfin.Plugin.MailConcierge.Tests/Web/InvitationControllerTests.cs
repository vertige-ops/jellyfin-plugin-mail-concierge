using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MailConcierge.Accounts;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Session;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Data;
using Jellyfin.Plugin.MailConcierge.Email;
using Jellyfin.Plugin.MailConcierge.Invitations;
using Jellyfin.Plugin.MailConcierge.Security;
using Jellyfin.Plugin.MailConcierge.Seerr;
using Jellyfin.Plugin.MailConcierge.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Web;

public sealed class InvitationControllerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mailconcierge-invc-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private readonly PluginConfiguration _config = new() { PublicUrl = "https://jf.example/", DefaultLanguage = "en" };
    private readonly Guid _library = Guid.NewGuid();
    private readonly MailConciergeStore _store;
    private readonly EmailQueue _queue;
    private readonly InvitationController _controller;

    public InvitationControllerTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new MailConciergeStore(_dir);
        var tokens = new TokenService(_dir);
        var limiter = new RateLimiter(_time);
        _queue = new EmailQueue(() => _config, Mock.Of<IEmailTransport>(), limiter, NullLogger<EmailQueue>.Instance, _time, new[] { TimeSpan.Zero });
        var users = new Mock<IUserManager>();
        var host = Mock.Of<IServerApplicationHost>();
        var email = new EmailService(_store, tokens, limiter, _queue, users.Object, new SessionCloser(Mock.Of<IDeviceManager>(), Mock.Of<ISessionManager>(), _time, NullLogger<SessionCloser>.Instance), new NullSeerrSync(), host, () => _config, _time, NullLogger<EmailService>.Instance);
        var service = new InvitationService(_store, tokens, limiter, _queue, users.Object, email, host, () => _config, _time, NullLogger<InvitationService>.Instance);
        var libraries = new Mock<ILibraryManager>();
        libraries.Setup(l => l.GetVirtualFolders()).Returns(new System.Collections.Generic.List<VirtualFolderInfo> { new() { ItemId = _library.ToString("N"), Name = "Films" } });
        _controller = new InvitationController(service, libraries.Object, () => _config)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    public void Dispose()
    {
        _queue.Dispose();
        _store.Dispose();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task Create_ReturnsLink_AndList_NeverReturnsLinkOrHash()
    {
        var created = Assert.IsType<ContentResult>(await _controller.Create(new CreateInvitationRequest("alice", null, new[] { _library.ToString("N") }, null, "en")));
        var link = JsonDocument.Parse(created.Content!).RootElement.GetProperty("link").GetString()!;
        Assert.Contains("/MailConcierge/Accept?t=", link, StringComparison.Ordinal);

        var listed = Assert.IsType<ContentResult>(await _controller.List());

        var token = link[(link.IndexOf("t=", StringComparison.Ordinal) + 2)..];
        Assert.DoesNotContain(token, listed.Content!, StringComparison.Ordinal);
        Assert.DoesNotContain("hash", listed.Content!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("link", listed.Content!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"state\":\"Pending\"", listed.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_WithEmail_ReportsEmailedOnlyWhenTheMessageWasQueued()
    {
        _config.SmtpHost = "smtp.example";
        _config.FromAddress = "jf@example.com";
        _config.MaxInvitationEmailsPerHour = 1;

        var first = JsonDocument.Parse(Assert.IsType<ContentResult>(await _controller.Create(new CreateInvitationRequest("alice", "a@example.org", Array.Empty<string>(), null, "en"))).Content!).RootElement;
        var second = JsonDocument.Parse(Assert.IsType<ContentResult>(await _controller.Create(new CreateInvitationRequest("bob", "b@example.org", Array.Empty<string>(), null, "en"))).Content!).RootElement;

        Assert.True(first.GetProperty("emailed").GetBoolean());
        Assert.Equal("queued", first.GetProperty("emailStatus").GetString());
        Assert.False(second.GetProperty("emailed").GetBoolean());
        Assert.Equal("limit_reached", second.GetProperty("emailStatus").GetString());
        Assert.False(string.IsNullOrEmpty(second.GetProperty("link").GetString()));
    }

    [Fact]
    public async Task Create_WithEmail_WhenSmtpIsNotConfigured_ReportsNotConfigured()
    {
        var created = JsonDocument.Parse(Assert.IsType<ContentResult>(await _controller.Create(new CreateInvitationRequest("alice", "a@example.org", Array.Empty<string>(), null, "en"))).Content!).RootElement;

        Assert.False(created.GetProperty("emailed").GetBoolean());
        Assert.Equal("not_configured", created.GetProperty("emailStatus").GetString());
    }

    [Fact]
    public async Task Create_Anonymous_Exhaustion_DoesNotStarveInvitations()
    {
        _config.SmtpHost = "smtp.example";
        _config.FromAddress = "jf@example.com";
        _config.MaxRecoveryEmailsPerHour = 1;
        for (var i = 0; i < 3; i++)
        {
            _queue.Enqueue(new EmailMessage("x@example.org", "s", "<p>h</p>", "t", "recovery", Guid.NewGuid()));
        }

        var created = JsonDocument.Parse(Assert.IsType<ContentResult>(await _controller.Create(new CreateInvitationRequest("alice", "a@example.org", Array.Empty<string>(), null, "en"))).Content!).RootElement;

        Assert.True(created.GetProperty("emailed").GetBoolean());
    }

    [Fact]
    public async Task Create_UnknownLibrary_IsRefused()
    {
        var result = Assert.IsType<ContentResult>(await _controller.Create(new CreateInvitationRequest("alice", null, new[] { Guid.NewGuid().ToString("N") }, null, "en")));

        Assert.Equal(400, result.StatusCode);
    }

    [Fact]
    public void Libraries_ListsIdAndName()
    {
        var result = Assert.IsType<ContentResult>(_controller.Libraries());

        Assert.Contains("\"name\":\"Films\"", result.Content!, StringComparison.Ordinal);
        Assert.Contains(_library.ToString("N"), result.Content!, StringComparison.Ordinal);
    }
}
