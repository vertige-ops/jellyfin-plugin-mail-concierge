using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
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
using Jellyfin.Plugin.MailConcierge.Web;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Web;

public sealed class ForgotPasswordFilterTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mailconcierge-filter-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _fake = new(new DateTimeOffset(2026, 10, 31, 12, 0, 30, TimeSpan.Zero));
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

    private readonly CapturingTransport _transport = new();
    private readonly User _alice = Make("alice");
    private readonly User _pending = Make("pending");
    private readonly Mock<IUserManager> _users = new(MockBehavior.Strict);
    private readonly Mock<IServerConfigurationManager> _serverConfig = new();
    private readonly MailConciergeStore _store;
    private readonly TokenService _tokens;
    private readonly EmailQueue _queue;
    private readonly ProxyGuard _guard;
    private readonly RateLimiter _limiter;
    private readonly Mock<IServerApplicationHost> _host = new();
    private readonly Mock<IDeviceManager> _devices = new();
    private readonly Mock<ISessionManager> _sessions = new();

    public ForgotPasswordFilterTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new MailConciergeStore(_dir);
        _tokens = new TokenService(_dir);
        _limiter = new RateLimiter(_fake);
        _queue = new EmailQueue(() => _config, _transport, _limiter, NullLogger<EmailQueue>.Instance, _fake, new[] { TimeSpan.Zero });
        _queue.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        _store.WriteAsync(d =>
        {
            d.Emails.Add(new UserEmail(_alice.Id, "alice@example.org", null, "en", _fake.GetUtcNow().UtcDateTime));
            d.Emails.Add(new UserEmail(_pending.Id, null, "pending@example.org", "en", null));
        }).GetAwaiter().GetResult();
        _serverConfig.SetupGet(c => c.Configuration).Returns(new ServerConfiguration { UICulture = "fr-FR" });
        _serverConfig.Setup(c => c.GetConfiguration("network")).Returns(new NetworkConfiguration { KnownProxies = Array.Empty<string>() });
        _guard = new ProxyGuard(_serverConfig.Object);
        _host.SetupGet(h => h.FriendlyName).Returns("My Jellyfin");
        _users.Setup(u => u.GetUserByName("ghost")).Returns((User?)null);
        _users.Setup(u => u.GetUserByName("noemail")).Returns(Make("noemail"));
        _users.Setup(u => u.GetUserByName("pending")).Returns(_pending);
        _users.Setup(u => u.GetUserByName("alice")).Returns(_alice);
        var ldap = new User("ldap", "Jellyfin.Plugin.LDAP", "reset") { Id = Guid.NewGuid() };
        _users.Setup(u => u.GetUserByName("ldap")).Returns(ldap);
        var admin = Make("admin");
        admin.SetPermission(PermissionKind.IsAdministrator, true);
        _users.Setup(u => u.GetUserByName("admin")).Returns(admin);
    }

    public async ValueTask DisposeAsync()
    {
        // Let the fire-and-forget recovery work started by the filter finish before tearing the store down.
        await Task.Delay(200);
        await _queue.StopAsync(CancellationToken.None);
        _queue.Dispose();
        _store.Dispose();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task ForgotPassword_ResponseIsIdentical_ForAllUserStates()
    {
        var filter = MakeFilter(_fake);
        var json = new List<string>();
        var n = 1;
        // Each state comes from its own IP so that none is limited by accident; alice is the eligible one.
        foreach (var name in new[] { "ghost", "noemail", "pending", "admin", "ldap", "alice" })
        {
            var ctx = Context("ForgotPassword", name, "198.51.100." + n++);
            await Run(filter, ctx, _fake);
            json.Add(Serialize(ctx.Result));
        }

        // Rate-limited: a second call from the IP that already asked once, with a limit of one.
        _config.MaxForgotRequestsPerIpPer15Min = 1;
        var limited = Context("ForgotPassword", "alice", "198.51.100.6");
        await Run(filter, limited, _fake);
        json.Add(Serialize(limited.Result));

        var proxied = Context("ForgotPassword", "alice", "10.0.0.2", forwardedFor: true);
        await Run(filter, proxied, _fake);
        json.Add(Serialize(proxied.Result));

        Assert.True(_guard.Tripped);
        Assert.All(json, j => Assert.Equal(json[0], j));
        Assert.Contains("code", json[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForgotPassword_ForwardsEligibleRequestToRecoveryService()
    {
        var filter = MakeFilter(_fake);
        await Run(filter, Context("ForgotPassword", "alice", "203.0.113.7"), _fake);

        var mail = Assert.Single(await _transport.WaitForAsync(1));
        Assert.Equal("alice@example.org", mail.To);
        Assert.Equal("recovery", mail.Template);
        var stored = await _store.ReadAsync(d => d.Tokens.Count(t => t.UserId == _alice.Id && t.Purpose == TokenPurpose.ResetPin));
        Assert.Equal(1, stored);
        // The IP key reached the limiter: a second request from the same IP is refused when the limit is 1.
        _config.MaxForgotRequestsPerIpPer15Min = 1;
        await Run(filter, Context("ForgotPassword", "alice", "203.0.113.7"), _fake);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Single(_transport.Sent);
    }

    [Fact]
    public async Task ForgotPassword_UnsupportedServerLanguage_FallsBackToDefaultLanguage()
    {
        _serverConfig.SetupGet(c => c.Configuration).Returns(new ServerConfiguration { UICulture = "zh-CN" });
        _config.DefaultLanguage = "fr";
        var filter = MakeFilter(_fake);
        var ctx = Context("ForgotPassword", "ghost", "203.0.113.7");
        await Run(filter, ctx, _fake);
        Assert.Contains("code", Serialize(ctx.Result), StringComparison.Ordinal);
        Assert.Contains("Si ce compte", Serialize(ctx.Result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForgotPassword_TakesAtLeast300ms()
    {
        var filter = MakeFilter(TimeProvider.System);
        var ctx = Context("ForgotPassword", "ghost", "203.0.113.7");
        var sw = Stopwatch.StartNew();
        await filter.OnActionExecutionAsync(ctx, () => throw new InvalidOperationException("next must not run"));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 295, $"took {sw.ElapsedMilliseconds} ms");
        Assert.IsType<ObjectResult>(ctx.Result);
    }

    [Fact]
    public async Task ForgotPassword_WithoutRemoteAddress_StillAnswers()
    {
        var filter = MakeFilter(_fake);
        var ctx = Context("ForgotPassword", "ghost", null);
        await Run(filter, ctx, _fake);
        Assert.IsType<ObjectResult>(ctx.Result);
    }

    [Fact]
    public async Task ForgotPasswordPin_Returns404_AndNeverCallsUserManager()
    {
        var filter = MakeFilter(_fake);
        var ctx = Context("ForgotPasswordPin", null, "203.0.113.7");
        await filter.OnActionExecutionAsync(ctx, () => throw new InvalidOperationException("next must not run"));

        var result = Assert.IsType<ContentResult>(ctx.Result);
        Assert.Equal(404, result.StatusCode);
        Assert.Equal("text/plain", result.ContentType);
        Assert.Equal("Error processing request.", result.Content);
        _users.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Filter_PassesThrough_WhenNativeSwitchOn(bool native, bool enabled)
    {
        _config.NativeForgotPassword = native;
        _config.Enabled = enabled;
        var filter = MakeFilter(_fake);
        var called = false;
        foreach (var action in new[] { "ForgotPassword", "ForgotPasswordPin" })
        {
            var ctx = Context(action, "alice", "203.0.113.7");
            await filter.OnActionExecutionAsync(ctx, () =>
            {
                called = true;
                return Task.FromResult<ActionExecutedContext>(null!);
            });
            Assert.True(called);
            Assert.Null(ctx.Result);
            called = false;
        }
    }

    [Fact]
    public async Task GuardTripped_WhileEnabled_NeverHandsTheEndpointsToJellyfin()
    {
        var filter = MakeFilter(_fake);
        var neutral = Context("ForgotPassword", "ghost", "198.51.100.1");
        await Run(filter, neutral, _fake);
        Tests.Security.Guards.Trip(_guard);

        // Run throws if the native action is called.
        var forgot = Context("ForgotPassword", "alice", "203.0.113.7");
        await Run(filter, forgot, _fake);
        var pin = Context("ForgotPasswordPin", null, "203.0.113.7");
        await Run(filter, pin, _fake);

        Assert.Equal(Serialize(neutral.Result), Serialize(forgot.Result));
        Assert.Equal(404, Assert.IsType<ContentResult>(pin.Result).StatusCode);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Empty(_transport.Sent);
    }

    [Fact]
    public async Task Filter_IgnoresOtherActions()
    {
        var filter = MakeFilter(_fake);
        var ctx = Context("GetUsers", "alice", "203.0.113.7");
        var called = false;
        await filter.OnActionExecutionAsync(ctx, () =>
        {
            called = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });
        Assert.True(called);
        Assert.Null(ctx.Result);
    }

    private static User Make(string name)
        => new(name, AccountPolicy.DefaultAuthProviderId, "reset") { Id = Guid.NewGuid() };

    private static string Serialize(IActionResult? result)
        => JsonSerializer.Serialize(Assert.IsType<ObjectResult>(result).Value);

    private static ActionExecutingContext Context(string action, string? username, string? ip, bool forwardedFor = false)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = ip is null ? null : IPAddress.Parse(ip);
        if (forwardedFor)
        {
            http.Request.Headers["X-Forwarded-For"] = "198.51.100.9";
        }

        var descriptor = new ControllerActionDescriptor { ControllerName = "User", ActionName = action };
        var actionContext = new ActionContext(http, new RouteData(), descriptor);
        var args = new Dictionary<string, object?> { ["request"] = new FakeDto { EnteredUsername = username ?? string.Empty } };
        return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), args, new object());
    }

    private static async Task Run(ForgotPasswordFilter filter, ActionExecutingContext ctx, FakeTimeProvider time)
    {
        var task = filter.OnActionExecutionAsync(ctx, () => throw new InvalidOperationException("next must not run"));
        while (!task.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(1);
        }

        await task;
    }

    private ForgotPasswordFilter MakeFilter(TimeProvider time)
    {
        var closer = new SessionCloser(_devices.Object, _sessions.Object, time, NullLogger<SessionCloser>.Instance);
        var email = new EmailService(_store, _tokens, _limiter, _queue, _users.Object, closer, Mock.Of<ISeerrSync>(), _host.Object, () => _config, new ServiceGate(() => _config, _guard), time, NullLogger<EmailService>.Instance);
        var recovery = new RecoveryService(
            _store, _tokens, _limiter, _queue, new ServiceGate(() => _config, _guard), _users.Object, new EmailLoginResolver(_users.Object, email, () => _config), closer, _host.Object, () => _config, time, NullLogger<RecoveryService>.Instance);
        var services = new ServiceCollection();
        services.AddSingleton<Func<PluginConfiguration>>(() => _config);
        services.AddSingleton(_guard);
        services.AddSingleton(recovery);
        services.AddSingleton(new ForgotPasswordResponses(() => _config, time));
        services.AddSingleton(_serverConfig.Object);
        services.AddSingleton(time);
        return new ForgotPasswordFilter(services.BuildServiceProvider(), NullLogger<ForgotPasswordFilter>.Instance);
    }

    [Theory]
    [InlineData("ForgotPassword")]
    [InlineData("ForgotPasswordPin")]
    [InlineData("GetUsers")]
    public async Task UnavailableServices_PassThroughToNext(string action)
    {
        // Plugin.Instance is null (malfunctioned): the factories throw. The filter must never break the API.
        var services = new ServiceCollection();
        services.AddSingleton<Func<PluginConfiguration>>(_ => throw new InvalidOperationException("The Mail Concierge plugin is not initialized."));
        services.AddSingleton<RecoveryService>(_ => throw new InvalidOperationException("The Mail Concierge plugin is not initialized."));
        var filter = new ForgotPasswordFilter(services.BuildServiceProvider(), NullLogger<ForgotPasswordFilter>.Instance);
        var ctx = Context(action, "alice", "198.51.100.1");
        var calls = 0;

        await filter.OnActionExecutionAsync(ctx, () =>
        {
            calls++;
            return Task.FromResult<ActionExecutedContext>(null!);
        });
        await filter.OnActionExecutionAsync(ctx, () =>
        {
            calls++;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.Equal(2, calls);
        Assert.Null(ctx.Result);
    }

    private sealed class FakeDto
    {
        public string EnteredUsername { get; set; } = string.Empty;
    }

    private sealed class CapturingTransport : IEmailTransport
    {
        private readonly object _lock = new();
        private readonly List<EmailMessage> _sent = new();

        public IReadOnlyList<EmailMessage> Sent
        {
            get
            {
                lock (_lock)
                {
                    return _sent.ToArray();
                }
            }
        }

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
            for (var i = 0; i < 200 && Sent.Count < count; i++)
            {
                await Task.Delay(10);
            }

            return Sent;
        }
    }
}
