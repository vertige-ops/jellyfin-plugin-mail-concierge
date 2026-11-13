using System.Collections.Generic;

namespace Jellyfin.Plugin.MailConcierge.Web;

#pragma warning disable CA2227 // Deserialized JSON dictionary.
#pragma warning disable SA1402, SA1649 // Small related request types kept together.

/// <summary>
/// The body of <c>POST /MailConcierge/Admin/Config</c>. A missing (null) field keeps the stored value;
/// an empty secret keeps the stored secret.
/// </summary>
public sealed class AdminConfigRequest
{
    /// <summary>Gets or sets a value indicating whether Mail Concierge is enabled.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Gets or sets a value indicating whether the native forgot-password flow is replaced.</summary>
    public bool? NativeForgotPassword { get; set; }

    /// <summary>Gets or sets the public base URL (empty or absolute http/https).</summary>
    public string? PublicUrl { get; set; }

    /// <summary>Gets or sets the fallback language (fr or en).</summary>
    public string? DefaultLanguage { get; set; }

    /// <summary>Gets or sets the SMTP host.</summary>
    public string? SmtpHost { get; set; }

    /// <summary>Gets or sets the SMTP port.</summary>
    public int? SmtpPort { get; set; }

    /// <summary>Gets or sets the SMTP security (None, StartTls, SslOnConnect).</summary>
    public string? SmtpSecurity { get; set; }

    /// <summary>Gets or sets the SMTP user name.</summary>
    public string? SmtpUsername { get; set; }

    /// <summary>Gets or sets the SMTP password; empty keeps the stored one.</summary>
    public string? SmtpPassword { get; set; }

    /// <summary>Gets or sets a value indicating whether to empty the stored SMTP password; a non-empty <see cref="SmtpPassword"/> in the same request wins.</summary>
    public bool? ClearSmtpPassword { get; set; }

    /// <summary>Gets or sets the sender address.</summary>
    public string? FromAddress { get; set; }

    /// <summary>Gets or sets the sender display name.</summary>
    public string? FromName { get; set; }

    /// <summary>Gets or sets the PIN validity in minutes.</summary>
    public int? PinMinutes { get; set; }

    /// <summary>Gets or sets the invitation validity in days.</summary>
    public int? InvitationDays { get; set; }

    /// <summary>Gets or sets the e-mail confirmation validity in hours.</summary>
    public int? ConfirmationHours { get; set; }

    /// <summary>Gets or sets the validity in hours of the link cancelling an e-mail change.</summary>
    public int? CancelEmailChangeHours { get; set; }

    /// <summary>Gets or sets the recovery e-mails allowed per account per hour.</summary>
    public int? MaxRecoveryEmailsPerAccountPerHour { get; set; }

    /// <summary>Gets or sets the forgot-password requests allowed per IP per 15 minutes.</summary>
    public int? MaxForgotRequestsPerIpPer15Min { get; set; }

    /// <summary>Gets or sets the recovery e-mails allowed per hour server-wide.</summary>
    public int? MaxRecoveryEmailsPerHour { get; set; }

    /// <summary>Gets or sets the invitation e-mails allowed per hour server-wide.</summary>
    public int? MaxInvitationEmailsPerHour { get; set; }

    /// <summary>Gets or sets the confirmation and change notice e-mails allowed per hour server-wide.</summary>
    public int? MaxAccountEmailsPerHour { get; set; }

    /// <summary>Gets or sets the PIN failures after which the PIN is invalidated.</summary>
    public int? MaxPinFailures { get; set; }

    /// <summary>Gets or sets a value indicating whether administrators are handled.</summary>
    public bool? AllowAdministrators { get; set; }

    /// <summary>Gets or sets a value indicating whether users may sign in with their confirmed e-mail address; null keeps the stored value.</summary>
    public bool? EmailLogin { get; set; }

    /// <summary>Gets or sets the sessions policy (None, All, InactiveSinceDays).</summary>
    public string? SessionsOnReset { get; set; }

    /// <summary>Gets or sets the inactivity threshold in days.</summary>
    public int? SessionInactiveDays { get; set; }

    /// <summary>Gets or sets a value indicating whether the Seerr integration is enabled.</summary>
    public bool? SeerrEnabled { get; set; }

    /// <summary>Gets or sets the Seerr URL.</summary>
    public string? SeerrUrl { get; set; }

    /// <summary>Gets or sets the Seerr API key; empty keeps the stored one.</summary>
    public string? SeerrApiKey { get; set; }

    /// <summary>Gets or sets the template overrides keyed <c>{lang}/{template}.{html|txt}</c>; null keeps them, an empty value removes one.</summary>
    public Dictionary<string, string>? TemplateOverrides { get; set; }
}

/// <summary>
/// The body of <c>POST /MailConcierge/Admin/TestEmail</c>.
/// </summary>
/// <param name="To">The recipient.</param>
public sealed record TestEmailRequest(string? To);
