using System;
using System.IO;
using System.Linq;
using System.Text.Json;
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
using Jellyfin.Plugin.MailConcierge.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
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
    private readonly PluginConfiguration _config = new() { Enabled = true, PublicUrl = "https://jf.example/", DefaultLanguage = "en" };
    private readonly Guid _library = Guid.NewGuid();
    private readonly Guid _library2 = Guid.NewGuid();
    private readonly Mock<IUserManager> _users = new();
    private readonly MailConciergeStore _store;
    private readonly EmailQueue _queue;
    private readonly Mock<ILiveTvManager> _liveTv = new();
    private readonly InvitationController _controller;

    public InvitationControllerTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new MailConciergeStore(_dir);
        var tokens = new TokenService(_dir);
        var limiter = new RateLimiter(_time);
        _queue = new EmailQueue(() => _config, Mock.Of<IEmailTransport>(), NullLogger<EmailQueue>.Instance, _time, new[] { TimeSpan.Zero }, 5);
        var users = _users;
        var host = Mock.Of<IServerApplicationHost>();
        var gate = new ServiceGate(() => _config, Guards.Quiet());
        var email = new EmailService(_store, tokens, limiter, _queue, users.Object, new SessionCloser(Mock.Of<IDeviceManager>(), Mock.Of<ISessionManager>(), _time, NullLogger<SessionCloser>.Instance), new NullSeerrSync(), host, () => _config, gate, _time, NullLogger<EmailService>.Instance);
        var service = new InvitationService(_store, tokens, limiter, _queue, users.Object, Mock.Of<ISessionManager>(), email, host, () => _config, gate, _time, NullLogger<InvitationService>.Instance);
        var libraries = new Mock<ILibraryManager>();
        libraries.Setup(l => l.GetVirtualFolders()).Returns(new System.Collections.Generic.List<VirtualFolderInfo> { new() { ItemId = _library.ToString("N"), Name = "Films" }, new() { ItemId = _library2.ToString("N"), Name = "Series" } });
        SetLiveTv(0, 0);
        _controller = new InvitationController(service, libraries.Object, _liveTv.Object, _users.Object, () => _config)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private async Task<JsonElement> PostCreate(string username, string? email)
        => JsonDocument.Parse(Assert.IsType<ContentResult>(await _controller.Create(new CreateInvitationRequest(username, email, Array.Empty<string>(), null, "en"))).Content!).RootElement;

    private void FillQueue()
    {
        // The worker is not started, so the queue only fills up.
        while (_queue.Enqueue(new EmailMessage("x@example.org", "s", "<p>h</p>", "t", "email-changed", Guid.NewGuid())) == EnqueueResult.Queued)
        {
        }
    }

    public void Dispose()
    {
        _queue.Dispose();
        _store.Dispose();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task Delete_WithoutErase_Revokes_AndWithEraseTrue_ErasesTheRecord()
    {
        var kept = (await PostCreate("alice", null)).GetProperty("id").GetGuid();
        var erased = (await PostCreate("bob", null)).GetProperty("id").GetGuid();
        var revoked = (await PostCreate("carol", null)).GetProperty("id").GetGuid();

        foreach (var answer in new[] { await _controller.Revoke(revoked), await _controller.Revoke(kept, false) })
        {
            var ok = Assert.IsType<ContentResult>(answer);
            Assert.Equal(StatusCodes.Status200OK, ok.StatusCode);
            Assert.Equal("{}", ok.Content);
        }

        var done = Assert.IsType<ContentResult>(await _controller.Revoke(erased, true));
        Assert.Equal(StatusCodes.Status200OK, done.StatusCode);
        Assert.Equal("{}", done.Content);

        var listed = JsonDocument.Parse(Assert.IsType<ContentResult>(await _controller.List()).Content!).RootElement.EnumerateArray()
            .ToDictionary(i => i.GetProperty("id").GetGuid(), i => i.GetProperty("state").GetString());
        Assert.Equal(2, listed.Count);
        Assert.Equal("Revoked", listed[kept]);
        Assert.Equal("Revoked", listed[revoked]);
        Assert.DoesNotContain(erased, listed.Keys);
    }

    [Fact]
    public async Task Create_ReturnsLink_AndList_NeverReturnsLinkOrHash()
    {
        var created = Assert.IsType<ContentResult>(await _controller.Create(new CreateInvitationRequest("alice", null, new[] { _library.ToString("N") }, null, "en")));
        var link = JsonDocument.Parse(created.Content!).RootElement.GetProperty("link").GetString()!;
        Assert.Contains("/MailConcierge/Accept#t=", link, StringComparison.Ordinal);

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
        _config.MaxEmailsPerDay = 1;

        var first = JsonDocument.Parse(Assert.IsType<ContentResult>(await _controller.Create(new CreateInvitationRequest("alice", "a@example.org", Array.Empty<string>(), null, "en"))).Content!).RootElement;
        var second = JsonDocument.Parse(Assert.IsType<ContentResult>(await _controller.Create(new CreateInvitationRequest("bob", "b@example.org", Array.Empty<string>(), null, "en"))).Content!).RootElement;

        Assert.True(first.GetProperty("emailed").GetBoolean());
        Assert.Equal("queued", first.GetProperty("emailStatus").GetString());
        Assert.False(second.GetProperty("emailed").GetBoolean());
        Assert.Equal("limit_reached", second.GetProperty("emailStatus").GetString());
        Assert.False(string.IsNullOrEmpty(second.GetProperty("link").GetString()));
    }

    [Fact]
    public async Task Create_WhenQueueFull_ReturnsLinkAndQueueFullStatus()
    {
        _config.SmtpHost = "smtp.example";
        _config.FromAddress = "jf@example.com";
        FillQueue();

        var body = await PostCreate("bob", "bob@example.org");

        Assert.False(body.GetProperty("emailed").GetBoolean());
        Assert.Equal("queue_full", body.GetProperty("emailStatus").GetString());
        Assert.StartsWith("https://jf.example/", body.GetProperty("link").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Regenerate_ReturnsEmailStatus()
    {
        _config.SmtpHost = "smtp.example";
        _config.FromAddress = "jf@example.com";
        var withAddress = await PostCreate("bob", "bob@example.org");
        var withoutAddress = await PostCreate("carol", null);

        var sent = JsonDocument.Parse(Assert.IsType<ContentResult>(await _controller.Regenerate(withAddress.GetProperty("id").GetGuid())).Content!).RootElement;
        var none = JsonDocument.Parse(Assert.IsType<ContentResult>(await _controller.Regenerate(withoutAddress.GetProperty("id").GetGuid())).Content!).RootElement;
        FillQueue();
        var full = JsonDocument.Parse(Assert.IsType<ContentResult>(await _controller.Regenerate(withAddress.GetProperty("id").GetGuid())).Content!).RootElement;

        Assert.True(sent.GetProperty("emailed").GetBoolean());
        Assert.Equal("queued", sent.GetProperty("emailStatus").GetString());
        Assert.False(none.GetProperty("emailed").GetBoolean());
        Assert.Equal("none", none.GetProperty("emailStatus").GetString());
        Assert.False(full.GetProperty("emailed").GetBoolean());
        Assert.Equal("queue_full", full.GetProperty("emailStatus").GetString());
        Assert.StartsWith("https://jf.example/", full.GetProperty("link").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_WithEmail_WhenSmtpIsNotConfigured_ReportsNotConfigured()
    {
        var created = JsonDocument.Parse(Assert.IsType<ContentResult>(await _controller.Create(new CreateInvitationRequest("alice", "a@example.org", Array.Empty<string>(), null, "en"))).Content!).RootElement;

        Assert.False(created.GetProperty("emailed").GetBoolean());
        Assert.Equal("not_configured", created.GetProperty("emailStatus").GetString());
    }

    [Fact]
    public async Task Create_UnknownLibrary_IsRefused()
    {
        var result = Assert.IsType<ContentResult>(await _controller.Create(new CreateInvitationRequest("alice", null, new[] { Guid.NewGuid().ToString("N") }, null, "en")));

        Assert.Equal(400, result.StatusCode);
    }

    [Fact]
    public async Task Closed_Create_Returns409ServiceClosed()
    {
        var created = JsonDocument.Parse(Assert.IsType<ContentResult>(await _controller.Create(new CreateInvitationRequest("alice", null, Array.Empty<string>(), null, "en"))).Content!).RootElement;
        _config.Enabled = false;

        var create = Assert.IsType<ContentResult>(await _controller.Create(new CreateInvitationRequest("bob", null, Array.Empty<string>(), null, "en")));
        var regenerate = Assert.IsType<ContentResult>(await _controller.Regenerate(created.GetProperty("id").GetGuid()));

        foreach (var result in new[] { create, regenerate })
        {
            Assert.Equal(409, result.StatusCode);
            Assert.Equal("service_closed", JsonDocument.Parse(result.Content!).RootElement.GetProperty("code").GetString());
        }
    }

    [Fact]
    public void Libraries_ListsIdAndName()
    {
        var result = Assert.IsType<ContentResult>(_controller.Libraries());

        Assert.Contains("\"name\":\"Films\"", result.Content!, StringComparison.Ordinal);
        Assert.Contains(_library.ToString("N"), result.Content!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 1, true)]
    [InlineData(2, 0, true)]
    [InlineData(0, 1, true)]
    public void LiveTv_FollowsJellyfinRule(int services, int enabledUsers, bool expected)
    {
        SetLiveTv(services, enabledUsers);

        var result = Assert.IsType<ContentResult>(_controller.LiveTv());

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Equal(expected, JsonDocument.Parse(result.Content!).RootElement.GetProperty("available").GetBoolean());
        Assert.Equal("no-store", _controller.Response.Headers.CacheControl.ToString());
    }

    private void SetLiveTv(int services, int enabledUsers)
        => _liveTv.Setup(l => l.GetLiveTvInfo(It.IsAny<CancellationToken>())).Returns(new LiveTvInfo
        {
            Services = Enumerable.Range(0, services).Select(i => new LiveTvServiceInfo { Name = "svc" + i }).ToArray(),
            EnabledUsers = Enumerable.Range(0, enabledUsers).Select(_ => Guid.NewGuid().ToString("N")).ToArray()
        });

    [Fact]
    public async Task Create_WithLiveTvTrue_StoresTrue()
    {
        await _controller.Create(new CreateInvitationRequest("alice", null, Array.Empty<string>(), null, "en", true));

        Assert.True((await _store.ReadAsync(d => d.Invitations.Single())).LiveTv);
    }

    [Fact]
    public async Task Create_WithoutLiveTvField_StoresFalse()
    {
        await PostCreate("alice", null);

        Assert.False((await _store.ReadAsync(d => d.Invitations.Single())).LiveTv);
    }

    private async Task<Guid> Seed(string username, InvitationState state, Guid? userId = null, DateTime? acceptedAt = null, string[]? libraryIds = null, bool? liveTv = null)
    {
        var id = (await PostCreate(username, null)).GetProperty("id").GetGuid();
        await _store.WriteAsync(d =>
        {
            var index = d.Invitations.FindIndex(i => i.Id == id);
            d.Invitations[index] = d.Invitations[index] with { State = state, UserId = userId, AcceptedAt = acceptedAt, LibraryIds = libraryIds ?? Array.Empty<string>(), LiveTv = liveTv };
        });
        return id;
    }

    private User KnownUser(string name)
    {
        var user = new User(name, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", MailConciergePasswordResetProvider.Id) { Id = Guid.NewGuid() };
        _users.Setup(u => u.GetUserById(user.Id)).Returns(user);
        _users.Setup(u => u.GetUserByName(name)).Returns(user);
        return user;
    }

    private async Task<JsonElement> ListedItem(Guid id)
    {
        var result = Assert.IsType<ContentResult>(await _controller.List());
        Assert.Equal("no-store", _controller.Response.Headers.CacheControl.ToString());
        return JsonDocument.Parse(result.Content!).RootElement.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id).Clone();
    }

    [Fact]
    public async Task List_KeepsTheExistingFields()
    {
        var id = await Seed("alice", InvitationState.Pending, libraryIds: new[] { _library.ToString("N") });

        var item = await ListedItem(id);

        Assert.Equal("alice", item.GetProperty("proposedUsername").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("email").ValueKind);
        Assert.Equal(_library.ToString("N"), item.GetProperty("libraryIds")[0].GetString());
        Assert.Equal("en", item.GetProperty("language").GetString());
        Assert.Equal("Pending", item.GetProperty("state").GetString());
        Assert.True(item.TryGetProperty("expiresAt", out _));
        Assert.True(item.TryGetProperty("createdAt", out _));
    }

    [Fact]
    public async Task List_ReturnsTheLibraryNames_InOrder_SkippingTheOnesDeletedSince()
    {
        var id = await Seed("alice", InvitationState.Pending, libraryIds: new[] { _library2.ToString("N"), Guid.NewGuid().ToString("N"), _library.ToString("N"), "not-a-guid" });

        var item = await ListedItem(id);

        Assert.Equal(new[] { "Series", "Films" }, item.GetProperty("libraries").EnumerateArray().Select(l => l.GetString()).ToArray());
        Assert.Equal(4, item.GetProperty("libraryIds").GetArrayLength());
    }

    [Fact]
    public async Task List_WithNullLibraryIds_DoesNotThrow_AndReturnsNoLibraries()
    {
        var id = await Seed("alice", InvitationState.Pending);
        await _store.WriteAsync(d =>
        {
            var index = d.Invitations.FindIndex(i => i.Id == id);
            d.Invitations[index] = d.Invitations[index] with { LibraryIds = null! };
        });

        Assert.Equal(0, (await ListedItem(id)).GetProperty("libraries").GetArrayLength());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task List_ReturnsLiveTv_TrueFalseOrNull(bool? liveTv)
    {
        var id = await Seed("alice", InvitationState.Pending, liveTv: liveTv);

        var value = (await ListedItem(id)).GetProperty("liveTv");

        Assert.Equal(liveTv is null ? JsonValueKind.Null : liveTv.Value ? JsonValueKind.True : JsonValueKind.False, value.ValueKind);
    }

    [Fact]
    public async Task List_ReturnsAcceptedAtInUtc_OrNull()
    {
        var at = new DateTime(2026, 11, 1, 8, 30, 0, DateTimeKind.Unspecified);
        var accepted = await Seed("alice", InvitationState.Accepted, acceptedAt: at);
        var pending = await Seed("bob", InvitationState.Pending);

        Assert.Equal("2026-11-01T08:30:00Z", (await ListedItem(accepted)).GetProperty("acceptedAt").GetString());
        Assert.Equal(JsonValueKind.Null, (await ListedItem(pending)).GetProperty("acceptedAt").ValueKind);
    }

    [Fact]
    public async Task List_Accepted_WithAnExistingAccount_ReturnsItsIdInNFormat()
    {
        var user = KnownUser("alice");
        var id = await Seed("alice", InvitationState.Accepted, userId: user.Id);

        Assert.Equal(user.Id.ToString("N"), (await ListedItem(id)).GetProperty("userId").GetString());
        _users.Verify(u => u.GetUserByName(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task List_Accepted_WhenTheAccountWasDeletedSince_ReturnsNoUserId()
    {
        var gone = Guid.NewGuid();
        var id = await Seed("alice", InvitationState.Accepted, userId: gone);

        Assert.Equal(JsonValueKind.Null, (await ListedItem(id)).GetProperty("userId").ValueKind);
        _users.Verify(u => u.GetUserByName(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task List_Accepted_WithoutUserId_ReturnsNoUserId_EvenWhenAnAccountOfThatNameExists()
    {
        KnownUser("alice");
        var id = await Seed("alice", InvitationState.Accepted, acceptedAt: DateTime.UtcNow);

        Assert.Equal(JsonValueKind.Null, (await ListedItem(id)).GetProperty("userId").ValueKind);
        _users.Verify(u => u.GetUserByName(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task List_Accepted_WithAStoredUserId_UsesItEvenWhenTheNameDiffers_WithoutLookingUpByName()
    {
        var user = KnownUser("renamed");
        var id = await Seed("alice", InvitationState.Accepted, userId: user.Id);

        Assert.Equal(user.Id.ToString("N"), (await ListedItem(id)).GetProperty("userId").GetString());
        _users.Verify(u => u.GetUserByName(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(InvitationState.Pending)]
    [InlineData(InvitationState.Revoked)]
    [InlineData(InvitationState.Expired)]
    public async Task List_NotAccepted_NeverReturnsAUserId_NorLooksUpByName(InvitationState state)
    {
        var user = KnownUser("alice");
        var id = await Seed("alice", state, userId: user.Id);
        var byName = await Seed("alice", state);

        Assert.Equal(JsonValueKind.Null, (await ListedItem(id)).GetProperty("userId").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await ListedItem(byName)).GetProperty("userId").ValueKind);
        _users.Verify(u => u.GetUserByName(It.IsAny<string>()), Times.Never);
        _users.Verify(u => u.GetUserById(It.IsAny<Guid>()), Times.Never);
    }
}
