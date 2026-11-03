using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MailConcierge.Accounts;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Data;
using Jellyfin.Plugin.MailConcierge.Seerr;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Seerr;

public sealed class SeerrSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mailconcierge-seerr-" + Guid.NewGuid().ToString("N"));
    private readonly PluginConfiguration _config = new() { SeerrEnabled = true, SeerrUrl = "https://seerr.example/", SeerrApiKey = "sekret-key" };
    private readonly FakeSeerr _seerr = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly MailConciergeStore _store;
    private readonly SeerrSync _sync;
    private readonly User _bob = new("bob", AccountPolicy.DefaultAuthProviderId, "reset") { Id = Guid.NewGuid() };
    private readonly User _eve = new("eve", AccountPolicy.DefaultAuthProviderId, "reset") { Id = Guid.NewGuid() };

    public SeerrSyncTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new MailConciergeStore(_dir);
        var client = new SeerrClient(new FakeFactory(_seerr), () => _config, NullLogger<SeerrClient>.Instance);
        _sync = new SeerrSync(_store, client, _users.Object, () => _config, TimeProvider.System, NullLogger<SeerrSync>.Instance);
        _users.Setup(u => u.GetUserById(_bob.Id)).Returns(_bob);
        _users.Setup(u => u.GetUserById(_eve.Id)).Returns(_eve);
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task SetEmail_PostsBackAllFields_OnlyEmailChanged()
    {
        _seerr.Users[7] = new FakeUser(_bob.Id, "bob");
        _seerr.Settings[7] = JsonNode.Parse("""{"username":"Bobby","email":"bob","locale":"fr","discoverRegion":"FR","streamingRegion":"FR","originalLanguage":"en","movieQuotaLimit":5,"movieQuotaDays":7,"tvQuotaLimit":null,"watchlistSyncMovies":true,"watchlistSyncTv":false,"globalMovieQuotaDays":1}""")!.AsObject();
        await _store.WriteAsync(d => d.Emails.Add(new UserEmail(_bob.Id, "bob@example.org", null, "fr", DateTime.UtcNow)));

        await _sync.PushAsync(_bob.Id);

        var post = Assert.Single(_seerr.Posts);
        Assert.Equal("/api/v1/user/7/settings/main", post.Path);
        var expected = JsonNode.Parse(_seerr.OriginalSettings[7].ToJsonString())!.AsObject();
        expected["email"] = "bob@example.org";
        Assert.Equal(expected.ToJsonString(), post.Body.ToJsonString());
        Assert.All(_seerr.Requests, r => Assert.Equal("sekret-key", r.ApiKey));
        Assert.Contains(_seerr.Requests, r => r.Path == "/api/v1/user/jellyfin/" + _bob.Id.ToString("N"));
    }

    [Fact]
    public async Task Push_UserMissingInSeerr_AddedToPending_ThenRetried()
    {
        await _store.WriteAsync(d => d.Emails.Add(new UserEmail(_bob.Id, "bob@example.org", null, "fr", DateTime.UtcNow)));

        await _sync.PushAsync(_bob.Id);

        Assert.Equal([_bob.Id], await _store.ReadAsync(d => d.PendingSeerr.ToList()));
        Assert.Empty(_seerr.Posts);

        _seerr.Users[9] = new FakeUser(_bob.Id, "bob");
        _seerr.Settings[9] = JsonNode.Parse("""{"username":"bob","email":"bob","locale":"fr"}""")!.AsObject();
        var done = await _sync.RetryPendingAsync();

        Assert.Equal(1, done);
        Assert.Empty(await _store.ReadAsync(d => d.PendingSeerr.ToList()));
        Assert.Equal("bob@example.org", (string?)_seerr.Posts.Single().Body["email"]);
    }

    [Fact]
    public async Task Push_Disabled_NoHttpCalls()
    {
        _config.SeerrEnabled = false;
        await _store.WriteAsync(d => d.Emails.Add(new UserEmail(_bob.Id, "bob@example.org", null, "fr", DateTime.UtcNow)));

        await _sync.PushAsync(_bob.Id);

        Assert.Empty(_seerr.Requests);
    }

    [Fact]
    public async Task Push_UnconfirmedEmail_NoHttpCalls()
    {
        await _store.WriteAsync(d => d.Emails.Add(new UserEmail(_bob.Id, null, "bob@example.org", "fr", null)));

        await _sync.PushAsync(_bob.Id);

        Assert.Empty(_seerr.Requests);
    }

    [Fact]
    public async Task Preview_SkipsPlaceholderEmailsAndUnlinkedUsers()
    {
        var unknown = Guid.NewGuid();
        _seerr.Users[1] = new FakeUser(_bob.Id, "bob@example.org");
        _seerr.Users[2] = new FakeUser(_eve.Id, "eve");
        _seerr.Users[3] = new FakeUser(unknown, "ghost@example.org");
        _seerr.Users[4] = new FakeUser(null, "local@example.org");

        var candidates = await _sync.PreviewImportAsync();

        var only = Assert.Single(candidates);
        Assert.Equal(_bob.Id, only.UserId);
        Assert.Equal("bob", only.Username);
        Assert.Equal("bob@example.org", only.Email);
    }

    [Fact]
    public async Task Preview_SkipsAddressesAlreadyStored_AndPagesThroughAllUsers()
    {
        await _store.WriteAsync(d => d.Emails.Add(new UserEmail(_bob.Id, "BOB@example.org", null, "fr", DateTime.UtcNow)));
        _seerr.Users[1] = new FakeUser(_bob.Id, "bob@example.org");
        for (var i = 10; i < 260; i++)
        {
            _seerr.Users[i] = new FakeUser(null, "u" + i + "@example.org");
        }

        _seerr.Users[300] = new FakeUser(_eve.Id, "eve@example.org");

        var candidates = await _sync.PreviewImportAsync();

        Assert.Equal(_eve.Id, Assert.Single(candidates).UserId);
        Assert.True(_seerr.Requests.Count(r => r.Path == "/api/v1/user") >= 3);
    }

    [Fact]
    public async Task Import_StoresConfirmed()
    {
        _seerr.Users[1] = new FakeUser(_bob.Id, "bob@example.org");
        _seerr.Users[2] = new FakeUser(_eve.Id, "eve@example.org");

        var count = await _sync.ApplyImportAsync([_bob.Id, Guid.NewGuid()]);

        Assert.Equal(1, count);
        var emails = await _store.ReadAsync(d => d.Emails.ToList());
        var stored = Assert.Single(emails);
        Assert.Equal(_bob.Id, stored.UserId);
        Assert.Equal("bob@example.org", stored.Email);
        Assert.Null(stored.PendingEmail);
        Assert.NotNull(stored.ConfirmedAt);
        Assert.Empty(_seerr.Posts);
    }

    [Fact]
    public async Task SeerrDown_DoesNotThrow()
    {
        _seerr.Down = true;
        await _store.WriteAsync(d => d.Emails.Add(new UserEmail(_bob.Id, "bob@example.org", null, "fr", DateTime.UtcNow)));

        await _sync.PushAsync(_bob.Id);
        Assert.Equal(0, await _sync.RetryPendingAsync());
    }

    [Fact]
    public async Task AdminActions_WhenSeerrIsDown_ReportUnreachable()
    {
        _seerr.Down = true;

        Assert.Equal("seerr_unreachable", (await Assert.ThrowsAsync<SeerrUnavailableException>(() => _sync.PreviewImportAsync())).Code);
        Assert.Equal("seerr_unreachable", (await Assert.ThrowsAsync<SeerrUnavailableException>(() => _sync.ApplyImportAsync([_bob.Id]))).Code);
        Assert.Equal("seerr_unreachable", (await Assert.ThrowsAsync<SeerrUnavailableException>(() => _sync.SyncNowAsync())).Code);
    }

    [Fact]
    public async Task AdminActions_WhenSeerrIsDisabled_ReportDisabled()
    {
        _config.SeerrEnabled = false;

        Assert.Equal("seerr_disabled", (await Assert.ThrowsAsync<SeerrUnavailableException>(() => _sync.PreviewImportAsync())).Code);
        Assert.Equal("seerr_disabled", (await Assert.ThrowsAsync<SeerrUnavailableException>(() => _sync.ApplyImportAsync([_bob.Id]))).Code);
        Assert.Equal("seerr_disabled", (await Assert.ThrowsAsync<SeerrUnavailableException>(() => _sync.SyncNowAsync())).Code);
    }

    [Fact]
    public async Task Controller_MapsUnavailableSeerr_To503WithCode()
    {
        var controller = new Jellyfin.Plugin.MailConcierge.Web.AccountController(null!, _sync, _users.Object, () => _config)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() },
        };

        _seerr.Down = true;
        var down = new[]
        {
            Assert.IsType<Microsoft.AspNetCore.Mvc.ContentResult>(await controller.SeerrImportPreview()),
            Assert.IsType<Microsoft.AspNetCore.Mvc.ContentResult>(await controller.SeerrSyncNow()),
            Assert.IsType<Microsoft.AspNetCore.Mvc.ContentResult>(await controller.SeerrImport(new Jellyfin.Plugin.MailConcierge.Web.SeerrImportRequest([_bob.Id]))),
        };
        Assert.All(down, r =>
        {
            Assert.Equal(503, r.StatusCode);
            Assert.Contains("seerr_unreachable", r.Content!, StringComparison.Ordinal);
        });

        _config.SeerrEnabled = false;
        var disabled = Assert.IsType<Microsoft.AspNetCore.Mvc.ContentResult>(await controller.SeerrImportPreview());
        Assert.Equal(503, disabled.StatusCode);
        Assert.Contains("seerr_disabled", disabled.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncNow_WhenReachable_PushesPending()
    {
        _seerr.Users[7] = new FakeUser(_bob.Id, "bob");
        _seerr.Settings[7] = JsonNode.Parse("""{"username":"bob","email":"bob"}""")!.AsObject();
        await _store.WriteAsync(d =>
        {
            d.Emails.Add(new UserEmail(_bob.Id, "bob@example.org", null, "fr", DateTime.UtcNow));
            d.PendingSeerr.Add(_bob.Id);
        });

        Assert.Equal(1, await _sync.SyncNowAsync());
    }

    [Fact]
    public async Task SetEmail_Seerr400_GivesUpWithoutThrowing()
    {
        _seerr.Users[7] = new FakeUser(_bob.Id, "bob");
        _seerr.Settings[7] = JsonNode.Parse("""{"username":"bob","email":"bob"}""")!.AsObject();
        _seerr.RejectPost = true;
        await _store.WriteAsync(d => d.Emails.Add(new UserEmail(_bob.Id, "bob@example.org", null, "fr", DateTime.UtcNow)));

        await _sync.PushAsync(_bob.Id);

        Assert.Empty(await _store.ReadAsync(d => d.PendingSeerr.ToList()));
    }

    private sealed record FakeUser(Guid? JellyfinUserId, string Email);

    private sealed record Seen(string Method, string Path, string? ApiKey);

    private sealed record Posted(string Path, JsonObject Body);

    private sealed class FakeFactory(FakeSeerr seerr) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(seerr, false);
    }

    private sealed class FakeSeerr : HttpMessageHandler
    {
        public Dictionary<int, FakeUser> Users { get; } = [];

        public Dictionary<int, JsonObject> Settings { get; } = [];

        public Dictionary<int, JsonObject> OriginalSettings { get; } = [];

        public List<Seen> Requests { get; } = [];

        public List<Posted> Posts { get; } = [];

        public bool Down { get; set; }

        public bool RejectPost { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down)
            {
                throw new HttpRequestException("connection refused");
            }

            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(new Seen(request.Method.Method, path, request.Headers.TryGetValues("X-Api-Key", out var k) ? k.Single() : null));
            const string jf = "/api/v1/user/jellyfin/";
            if (path.StartsWith(jf, StringComparison.Ordinal))
            {
                var id = Guid.Parse(path[jf.Length..]);
                var found = Users.Where(u => u.Value.JellyfinUserId == id).Select(u => (int?)u.Key).FirstOrDefault();
                return found is int n ? Json("""{"id":""" + n + "}") : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (path == "/api/v1/user")
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                var take = int.Parse(query["take"]!, System.Globalization.CultureInfo.InvariantCulture);
                var skip = int.Parse(query["skip"]!, System.Globalization.CultureInfo.InvariantCulture);
                var results = new JsonArray();
                foreach (var u in Users.OrderBy(u => u.Key).Skip(skip).Take(take))
                {
                    results.Add(new JsonObject
                    {
                        ["id"] = u.Key,
                        ["email"] = u.Value.Email,
                        ["jellyfinUserId"] = u.Value.JellyfinUserId?.ToString("N"),
                        ["userType"] = 3,
                    });
                }

                var body = new JsonObject { ["pageInfo"] = new JsonObject { ["results"] = Users.Count, ["pageSize"] = take }, ["results"] = results };
                return Json(body.ToJsonString());
            }

            var parts = path.Split('/');
            if (parts.Length == 7 && parts[3] == "user" && parts[5] == "settings" && parts[6] == "main" && int.TryParse(parts[4], out var seerrId) && Settings.TryGetValue(seerrId, out var settings))
            {
                if (request.Method == HttpMethod.Get)
                {
                    OriginalSettings[seerrId] = JsonNode.Parse(settings.ToJsonString())!.AsObject();
                    return Json(settings.ToJsonString());
                }

                if (RejectPost)
                {
                    return Json("""{"message":"Email already exists","type":"InvalidEmail"}""", HttpStatusCode.BadRequest);
                }

                var posted = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                Posts.Add(new Posted(path, posted));
                return Json(posted.ToJsonString());
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
