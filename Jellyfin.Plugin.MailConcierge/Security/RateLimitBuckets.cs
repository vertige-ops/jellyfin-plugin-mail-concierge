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
}
