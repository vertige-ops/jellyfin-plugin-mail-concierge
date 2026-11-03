using System;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MailConcierge.Accounts;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Data;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Accounts;

public class AccountPolicyTests
{
    private static User MakeUser(string provider = AccountPolicy.DefaultAuthProviderId, bool admin = false, bool disabled = false)
    {
        var user = new User("bob", provider, "reset");
        user.SetPermission(PermissionKind.IsAdministrator, admin);
        user.SetPermission(PermissionKind.IsDisabled, disabled);
        return user;
    }

    private static UserEmail Confirmed(Guid id) => new(id, "bob@example.org", null, "en", DateTime.UtcNow);

    [Fact]
    public void Eligible_RegularUserWithConfirmedEmail()
    {
        var u = MakeUser();
        Assert.True(AccountPolicy.IsEligible(u, Confirmed(u.Id), new PluginConfiguration()));
    }

    [Fact]
    public void NotEligible_WhenEmailPendingOnly()
    {
        var u = MakeUser();
        var pending = new UserEmail(u.Id, null, "bob@example.org", "en", null);
        Assert.False(AccountPolicy.IsEligible(u, pending, new PluginConfiguration()));
        Assert.False(AccountPolicy.IsEligible(u, null, new PluginConfiguration()));
    }

    [Fact]
    public void NotEligible_Admin_UnlessAllowed()
    {
        var u = MakeUser(admin: true);
        Assert.False(AccountPolicy.IsEligible(u, Confirmed(u.Id), new PluginConfiguration()));
        Assert.True(AccountPolicy.IsEligible(u, Confirmed(u.Id), new PluginConfiguration { AllowAdministrators = true }));
    }

    [Fact]
    public void NotEligible_LdapUser()
    {
        var u = MakeUser("Jellyfin.Plugin.LDAP_Auth.LdapAuthenticationProviderPlugin");
        Assert.False(AccountPolicy.IsEligible(u, Confirmed(u.Id), new PluginConfiguration { AllowAdministrators = true }));
    }

    [Fact]
    public void NotEligible_DisabledUser()
    {
        var u = MakeUser(disabled: true);
        Assert.False(AccountPolicy.IsEligible(u, Confirmed(u.Id), new PluginConfiguration()));
    }
}
