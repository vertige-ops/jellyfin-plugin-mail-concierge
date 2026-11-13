using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MailConcierge.Accounts;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Security;
using Jellyfin.Plugin.MailConcierge.Tests.Security;
using Jellyfin.Plugin.MailConcierge.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Web;

public class ClientScriptControllerTests
{
    private const string FlagsMarker = "const FLAGS = ";

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public void ClientScript_FlagIsTrue_OnlyWhenSettingGateAndProbeAgree(bool setting, bool gateOpen, bool probeAvailable, bool expected)
    {
        var config = new PluginConfiguration { Enabled = true, EmailLogin = setting };
        var guard = Guards.Quiet();
        if (!gateOpen)
        {
            Guards.Trip(guard);
        }

        var content = Script(config, guard, probeAvailable);

        var flags = FlagsLiteral(content);
        Assert.Equal(expected ? "{\"emailLogin\":true}" : "{\"emailLogin\":false}", flags);
    }

    [Fact]
    public void ClientScript_FlagsFollowTheCurrentSetting_OnEveryRequest()
    {
        var config = new PluginConfiguration { Enabled = true, EmailLogin = false };
        var guard = Guards.Quiet();
        var controller = Controller(config, guard, true);

        Assert.Equal("{\"emailLogin\":false}", FlagsLiteral(Content(controller)));
        config.EmailLogin = true;
        Assert.Equal("{\"emailLogin\":true}", FlagsLiteral(Content(controller)));
    }

    [Fact]
    public void ClientScript_KeepsNoPlaceholder()
    {
        var content = Script(new PluginConfiguration { Enabled = true, EmailLogin = true }, Guards.Quiet(), true);

        Assert.DoesNotContain("/*MAILCONCIERGE_", content, StringComparison.Ordinal);
        Assert.Contains(FlagsMarker, content, StringComparison.Ordinal);
    }

    internal static EmailLoginProbe Probe(bool available)
    {
        var actions = available
            ? new[]
            {
                new ControllerActionDescriptor
                {
                    ControllerName = EmailLoginProbe.ControllerName,
                    ActionName = EmailLoginProbe.ActionName,
                    Parameters = new List<ParameterDescriptor> { new() { Name = "request", ParameterType = typeof(SignInRequest) } },
                },
            }
            : Array.Empty<ControllerActionDescriptor>();
        var provider = new Mock<IActionDescriptorCollectionProvider>();
        provider.SetupGet(p => p.ActionDescriptors).Returns(new ActionDescriptorCollection(actions, 1));
        return new EmailLoginProbe(provider.Object);
    }

    private static ClientScriptController Controller(PluginConfiguration config, ProxyGuard guard, bool probeAvailable)
        => new(() => config, new ServiceGate(() => config, guard), Probe(probeAvailable))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static string Content(ClientScriptController controller)
        => Assert.IsType<ContentResult>(controller.Get()).Content!;

    private static string Script(PluginConfiguration config, ProxyGuard guard, bool probeAvailable)
        => Content(Controller(config, guard, probeAvailable));

    private static string FlagsLiteral(string content)
    {
        var start = content.IndexOf(FlagsMarker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += FlagsMarker.Length;
        var end = content.IndexOf(" || {};", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return content[start..end];
    }

    public sealed class SignInRequest
    {
        public string? Username { get; set; }
    }
}
