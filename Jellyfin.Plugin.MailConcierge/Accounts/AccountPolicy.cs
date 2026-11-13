using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Data;

namespace Jellyfin.Plugin.MailConcierge.Accounts;

/// <summary>
/// Decides which accounts Mail Concierge handles: recovery e-mails, and sign-in with the e-mail address.
/// </summary>
public static class AccountPolicy
{
    /// <summary>The only authentication provider Mail Concierge handles.</summary>
    public const string DefaultAuthProviderId = "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider";

    /// <summary>
    /// Returns whether the account is eligible for recovery e-mails and for sign-in with its e-mail address: local provider,
    /// confirmed address, not disabled, and administrator only when <see cref="PluginConfiguration.AllowAdministrators"/> is on.
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

    /// <summary>
    /// Tells whether one more failed password check would make Jellyfin disable the account: its failure counter never decays,
    /// and the account is disabled once the counter reaches the lockout threshold.
    /// </summary>
    /// <param name="invalidLoginAttemptCount">The failure counter kept by Jellyfin for the account.</param>
    /// <param name="loginAttemptsBeforeLockout">The lockout threshold of the account, or null when it has none.</param>
    /// <returns>True when the next failure would reach the threshold.</returns>
    public static bool NextFailureLocksOut(int invalidLoginAttemptCount, int? loginAttemptsBeforeLockout)
        => loginAttemptsBeforeLockout is not null && invalidLoginAttemptCount + 1 >= loginAttemptsBeforeLockout;
}
