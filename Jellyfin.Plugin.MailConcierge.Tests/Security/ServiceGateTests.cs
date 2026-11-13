using System;
using System.Net;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Security;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Security;

public class ServiceGateTests
{
    [Fact]
    public void Open_WhenEnabledAndGuardQuiet()
    {
        var config = new PluginConfiguration { Enabled = true };
        var gate = new ServiceGate(() => config, Guards.Quiet());

        Assert.True(gate.IsOpen);
    }

    [Fact]
    public void Closed_WhenDisabled()
    {
        var config = new PluginConfiguration { Enabled = false };
        var gate = new ServiceGate(() => config, Guards.Quiet());

        Assert.False(gate.IsOpen);
    }

    [Fact]
    public void Closed_WhenGuardTripped()
    {
        var config = new PluginConfiguration { Enabled = true };
        var guard = Guards.Quiet();
        var gate = new ServiceGate(() => config, guard);
        Assert.True(gate.IsOpen);

        Guards.Trip(guard);

        Assert.False(gate.IsOpen);
    }

    [Fact]
    public void Follows_TheCurrentConfiguration()
    {
        var config = new PluginConfiguration { Enabled = true };
        var gate = new ServiceGate(() => config, Guards.Quiet());

        config.Enabled = false;
        Assert.False(gate.IsOpen);
        config.Enabled = true;
        Assert.True(gate.IsOpen);
    }
}

/// <summary>Builds proxy guards for the tests: quiet, then tripped on demand like a real unknown proxy does.</summary>
internal static class Guards
{
    public static ProxyGuard Quiet()
    {
        var serverConfig = new Mock<IServerConfigurationManager>();
        serverConfig.Setup(c => c.GetConfiguration("network")).Returns(new NetworkConfiguration { KnownProxies = Array.Empty<string>() });
        return new ProxyGuard(serverConfig.Object);
    }

    public static void Trip(ProxyGuard guard)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.2");
        ctx.Request.Headers["X-Forwarded-For"] = "203.0.113.5";
        guard.Observe(ctx);
        Assert.True(guard.Tripped);
    }
}
