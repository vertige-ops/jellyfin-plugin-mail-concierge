using System.Collections.Frozen;

namespace Jellyfin.Plugin.MailConcierge.Security;

/// <summary>
/// Names of the rate limit buckets.
/// </summary>
public static class RateLimitBuckets
{
    /// <summary>Forgot-password requests per client address.</summary>
    public const string ForgotIp = "forgot-ip";

    /// <summary>Recovery e-mails per account.</summary>
    public const string ForgotAccount = "forgot-account";

    /// <summary>Recovery e-mails sent server-wide.</summary>
    public const string RecoveryEmail = "email-recovery";

    /// <summary>Invitation e-mails sent server-wide.</summary>
    public const string InvitationEmail = "email-invitation";

    /// <summary>Confirmation and change notice e-mails sent server-wide.</summary>
    public const string AccountEmail = "email-account";

    /// <summary>Failed PIN attempts per client address.</summary>
    public const string PinFailIp = "pin-fail-ip";

    /// <summary>Failed token attempts per client address.</summary>
    public const string TokenFailIp = "token-fail-ip";

    /// <summary>User name availability checks per invitation token.</summary>
    public const string UsernameCheckToken = "username-check-token";

    /// <summary>E-mail change requests per user.</summary>
    public const string EmailChangeUser = "email-change-user";

    /// <summary>Wrong current-password attempts per user, shared by every Mail Concierge form that checks the password.</summary>
    public const string PasswordFailUser = "password-fail-user";

    /// <summary>Public page views per client address, shared by the four pages.</summary>
    public const string PageViewIp = "page-view-ip";

    // The buckets whose key is a client address (every call site passes the client key), and only those.
    private static readonly FrozenSet<string> PerClientAddress = FrozenSet.Create(ForgotIp, PinFailIp, TokenFailIp, PageViewIp);

    /// <summary>
    /// Tells whether the keys of a bucket are client addresses. Such entries are evicted first when the limiter is full, so that a
    /// flood of distinct client addresses cannot make it forget the server-wide and per-account budgets.
    /// </summary>
    /// <param name="bucket">The bucket name.</param>
    /// <returns>True for a bucket keyed by client address.</returns>
    internal static bool IsPerClientAddress(string bucket) => PerClientAddress.Contains(bucket);
}
