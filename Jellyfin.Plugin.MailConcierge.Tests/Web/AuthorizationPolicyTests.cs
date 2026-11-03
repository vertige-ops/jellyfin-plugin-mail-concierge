using System;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.MailConcierge.Web;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Web;

/// <summary>
/// Guards the authorization of the administration endpoints: a removed or mistyped attribute would expose them.
/// </summary>
public class AuthorizationPolicyTests
{
    private static readonly string[] AdminActionsOfAccountController =
    {
        nameof(AccountController.ListEmails),
        nameof(AccountController.SetForUser),
        nameof(AccountController.SeerrSyncNow),
        nameof(AccountController.SeerrImportPreview),
        nameof(AccountController.SeerrImport),
    };

    [Theory]
    [InlineData(typeof(AdminController))]
    [InlineData(typeof(InvitationController))]
    public void Controller_RequiresElevation_AndNoActionOpensItUp(Type controller)
    {
        Assert.True(HasElevation(controller.GetCustomAttributes<AuthorizeAttribute>()), controller.Name + " must require elevation");
        Assert.Empty(controller.GetCustomAttributes<AllowAnonymousAttribute>());
        foreach (var action in Actions(controller))
        {
            Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>());
        }
    }

    [Fact]
    public void AccountController_AdminActions_RequireElevation()
    {
        var actions = Actions(typeof(AccountController)).ToDictionary(a => a.Name);
        foreach (var name in AdminActionsOfAccountController)
        {
            Assert.True(actions.TryGetValue(name, out var action), name + " not found");
            Assert.True(HasElevation(action!.GetCustomAttributes<AuthorizeAttribute>()), name + " must require elevation");
        }
    }

    [Fact]
    public void AccountController_EveryAction_IsAuthorized_AndNoneAnonymous()
    {
        var controller = typeof(AccountController);
        Assert.Empty(controller.GetCustomAttributes<AllowAnonymousAttribute>());
        foreach (var action in Actions(controller))
        {
            Assert.NotEmpty(action.GetCustomAttributes<AuthorizeAttribute>().Concat(controller.GetCustomAttributes<AuthorizeAttribute>()));
            Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>());
        }
    }

    [Fact]
    public void RequiresElevation_IsTheJellyfinPolicyName()
        => Assert.Equal("RequiresElevation", Policies.RequiresElevation);

    private static bool HasElevation(System.Collections.Generic.IEnumerable<AuthorizeAttribute> attributes)
        => attributes.Any(a => string.Equals(a.Policy, Policies.RequiresElevation, StringComparison.Ordinal));

    private static System.Collections.Generic.IEnumerable<MethodInfo> Actions(Type controller)
        => controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any());
}
