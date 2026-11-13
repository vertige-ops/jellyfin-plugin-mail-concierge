using System;
using System.Net;
using Jellyfin.Plugin.MailConcierge.Security;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Security;

public class ProxyGuardTests
{
    private static (ProxyGuard Guard, NetworkConfiguration Net) Make(params string[] knownProxies)
        => Make(new FakeTimeProvider(), knownProxies);

    private static (ProxyGuard Guard, NetworkConfiguration Net) Make(TimeProvider time, params string[] knownProxies)
    {
        var net = new NetworkConfiguration { KnownProxies = knownProxies };
        var cfg = new Mock<IServerConfigurationManager>();
        cfg.Setup(c => c.GetConfiguration("network")).Returns(() => net);
        return (new ProxyGuard(cfg.Object, time), net);
    }

    private static DefaultHttpContext Ctx(string? peer, bool forwarded)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = peer is null ? null : IPAddress.Parse(peer);
        if (forwarded)
        {
            ctx.Request.Headers["X-Forwarded-For"] = "203.0.113.5";
        }

        return ctx;
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("172.20.0.1")]
    [InlineData("192.168.1.10")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:192.168.1.10")]
    [InlineData("100.64.0.1")]
    [InlineData("100.100.100.100")]
    [InlineData("100.127.255.254")]
    [InlineData("::ffff:100.64.0.1")]
    public void ProxyGuard_Trips_WhenPrivatePeerSendsForwardedFor_AndNoKnownProxies(string peer)
    {
        var (guard, _) = Make();
        guard.Observe(Ctx(peer, true));
        Assert.True(guard.Tripped);
        Assert.Contains("Known Proxies", guard.Reason, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ProxyGuard_DoesNotTrip_WhenKnownProxiesSet()
    {
        var (guard, _) = Make("192.168.1.10");
        guard.Observe(Ctx("192.168.1.10", true));
        Assert.False(guard.Tripped);
        Assert.Null(guard.Reason);
    }

    [Theory]
    [InlineData("203.0.113.9", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("100.63.255.255", true)]
    [InlineData("100.128.0.0", true)]
    [InlineData("10.0.0.1", false)]
    [InlineData(null, true)]
    public void ProxyGuard_DoesNotTrip_ForPublicPeerNoHeaderOrNullPeer(string? peer, bool forwarded)
    {
        var (guard, _) = Make();
        guard.Observe(Ctx(peer, forwarded));
        Assert.False(guard.Tripped);
    }

    [Fact]
    public void ProxyGuard_StaysTripped_UntilKnownProxiesSet()
    {
        var (guard, net) = Make();
        guard.Observe(Ctx("10.0.0.1", true));
        guard.Observe(Ctx("203.0.113.9", false));
        Assert.True(guard.Tripped);
        net.KnownProxies = new[] { "10.0.0.1" };
        guard.Observe(Ctx("203.0.113.9", false));
        Assert.False(guard.Tripped);
    }

    [Fact]
    public void ProxyGuard_ExpiresByItself_AfterAWhileWithoutANewObservation()
    {
        var time = new FakeTimeProvider();
        var (guard, _) = Make(time);
        guard.Observe(Ctx("10.0.0.1", true));
        Assert.True(guard.Tripped);

        time.Advance(TimeSpan.FromMinutes(14));
        Assert.True(guard.Tripped);

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(guard.Tripped);
        Assert.Null(guard.Reason);
        Assert.Null(guard.ReasonCode);
    }

    [Fact]
    public void ProxyGuard_EachNewObservation_RestartsTheExpiry_AndTripsAgainAfterExpiry()
    {
        var time = new FakeTimeProvider();
        var (guard, _) = Make(time);
        guard.Observe(Ctx("10.0.0.1", true));
        time.Advance(TimeSpan.FromMinutes(10));
        guard.Observe(Ctx("10.0.0.1", true));
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.True(guard.Tripped);

        time.Advance(TimeSpan.FromMinutes(10));
        Assert.False(guard.Tripped);
        guard.Observe(Ctx("10.0.0.1", true));
        Assert.True(guard.Tripped);
        Assert.Contains("Known Proxies", guard.Reason, StringComparison.Ordinal);
        Assert.Contains("minutes", guard.Reason, StringComparison.Ordinal);
    }
}
