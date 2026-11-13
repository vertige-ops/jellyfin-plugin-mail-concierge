using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MailConcierge.Accounts;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Accounts;

public class EmailLoginProbeTests
{
    [Fact]
    public void Available_WhenActionAndPropertyExist()
    {
        var probe = new EmailLoginProbe(Provider(Action("User", "AuthenticateUserByName", typeof(SignIn))));

        Assert.True(probe.IsAvailable);
    }

    [Fact]
    public void Unavailable_WhenActionIsMissing()
    {
        var probe = new EmailLoginProbe(Provider(
            Action("User", "AuthenticateWithQuickConnect", typeof(SignIn)),
            Action("Users", "AuthenticateUserByName", typeof(SignIn))));

        Assert.False(probe.IsAvailable);
    }

    [Theory]
    [InlineData(typeof(NoUsername))]
    [InlineData(typeof(IntUsername))]
    [InlineData(typeof(ReadOnlyUsername))]
    public void Unavailable_WhenPropertyIsMissingOrNotAString(Type requestType)
    {
        var probe = new EmailLoginProbe(Provider(Action("User", "AuthenticateUserByName", requestType)));

        Assert.False(probe.IsAvailable);
    }

    [Fact]
    public void Unavailable_WhenTheProviderThrows()
    {
        var provider = new Mock<IActionDescriptorCollectionProvider>();
        provider.SetupGet(p => p.ActionDescriptors).Throws(new InvalidOperationException("not ready"));
        var probe = new EmailLoginProbe(provider.Object);

        Assert.False(probe.IsAvailable);
    }

    [Fact]
    public void IsAvailable_IsComputedOnce()
    {
        var provider = new Mock<IActionDescriptorCollectionProvider>();
        provider.SetupGet(p => p.ActionDescriptors).Returns(new ActionDescriptorCollection(new[] { Action("User", "AuthenticateUserByName", typeof(SignIn)) }, 1));
        var probe = new EmailLoginProbe(provider.Object);

        Assert.True(probe.IsAvailable);
        Assert.True(probe.IsAvailable);
        provider.VerifyGet(p => p.ActionDescriptors, Times.Once);
    }

    [Fact]
    public void FindUsernameProperty_ReturnsTheWritableStringProperty()
    {
        var property = EmailLoginProbe.FindUsernameProperty(new SignIn { Username = "alice" });

        Assert.NotNull(property);
        Assert.Equal("Username", property.Name);
    }

    [Fact]
    public void FindUsernameProperty_IgnoresReadOnlyProperties()
    {
        Assert.Null(EmailLoginProbe.FindUsernameProperty(new ReadOnlyUsername()));
        Assert.Null(EmailLoginProbe.FindUsernameProperty(new PrivateSetter()));
        Assert.Null(EmailLoginProbe.FindUsernameProperty(new IntUsername()));
        Assert.Null(EmailLoginProbe.FindUsernameProperty(null));
    }

    [Fact]
    public void FindUsernameProperty_HiddenProperty_ReturnsNull()
    {
        // A derived type that hides Username with `new` makes the lookup by name ambiguous.
        Assert.Null(EmailLoginProbe.FindUsernameProperty(new HidingUsername()));
    }

    [Fact]
    public void Unavailable_WhenThePropertyIsHidden()
    {
        var probe = new EmailLoginProbe(Provider(Action("User", "AuthenticateUserByName", typeof(HidingUsername))));

        Assert.False(probe.IsAvailable);
    }

    private static IActionDescriptorCollectionProvider Provider(params ActionDescriptor[] actions)
    {
        var provider = new Mock<IActionDescriptorCollectionProvider>();
        provider.SetupGet(p => p.ActionDescriptors).Returns(new ActionDescriptorCollection(actions, 1));
        return provider.Object;
    }

    private static ControllerActionDescriptor Action(string controller, string action, Type requestType) => new()
    {
        ControllerName = controller,
        ActionName = action,
        Parameters = new List<ParameterDescriptor> { new() { Name = "request", ParameterType = requestType } },
    };

    public sealed class SignIn
    {
        public string? Username { get; set; }

        public string? Pw { get; set; }
    }

    public sealed class NoUsername
    {
        public string? Name { get; set; }
    }

    public sealed class IntUsername
    {
        public int Username { get; set; }
    }

    public sealed class ReadOnlyUsername
    {
        public string Username => "alice";
    }

    public sealed class PrivateSetter
    {
        public string? Username { get; private set; }
    }

    public class BaseUsername
    {
        public string? Username { get; set; }
    }

    public sealed class HidingUsername : BaseUsername
    {
        public new int Username { get; set; }
    }
}
