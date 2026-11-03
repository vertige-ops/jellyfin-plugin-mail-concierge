using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Data;

namespace Jellyfin.Plugin.MailConcierge.Accounts;

/// <summary>
/// Decides which accounts may receive recovery e-mails.
/// </summary>
public static class AccountPolicy
{
    /// <summary>The only authentication provider Mail Concierge handles.</summary>
    public const string DefaultAuthProviderId = "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider";

    /// <summary>
    /// Returns whether the account is eligible for recovery e-mails.
    /// </summary>
    /// <param name="user">The Jellyfin user.</param>
    /// <param name="email">The stored e-mail settings of the user, if any.</param>
    /// <param name="cfg">The plugin configuration.</param>
    /// <returns>True when the account is eligible.</returns>
    public static bool IsEligible(User user, UserEmail? email, PluginConfiguration cfg)
    {
        System.ArgumentNullException.ThrowIfNull(user);
        System.ArgumentNullException.ThrowIfNull(cfg);

        return user.AuthenticationProviderId == DefaultAuthProviderId
            && !string.IsNullOrWhiteSpace(email?.Email)
            && email.ConfirmedAt is not null
            && (cfg.AllowAdministrators || !user.HasPermission(PermissionKind.IsAdministrator))
            && !user.HasPermission(PermissionKind.IsDisabled);
    }
}
