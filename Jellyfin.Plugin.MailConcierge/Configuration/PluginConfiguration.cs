using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MailConcierge.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether Mail Concierge is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a value indicating whether the native forgot-password flow of Jellyfin is used instead of Mail Concierge's e-mail recovery.</summary>
    public bool NativeForgotPassword { get; set; }

    /// <summary>Gets or sets a value indicating whether an account can sign in with its confirmed e-mail address in place of its user name.</summary>
    public bool EmailLogin { get; set; }

    /// <summary>Gets or sets the public base URL used to build every link in e-mails.</summary>
    public string PublicUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the fallback language code.</summary>
    public string DefaultLanguage { get; set; } = "en";

    /// <summary>Gets or sets the SMTP host.</summary>
    public string SmtpHost { get; set; } = string.Empty;

    /// <summary>Gets or sets the SMTP port.</summary>
    public int SmtpPort { get; set; } = 587;

    /// <summary>Gets or sets the SMTP transport security.</summary>
    public SmtpSecurity SmtpSecurity { get; set; } = SmtpSecurity.StartTls;

    /// <summary>Gets or sets the SMTP user name.</summary>
    public string SmtpUsername { get; set; } = string.Empty;

    /// <summary>Gets or sets the SMTP password.</summary>
    public string SmtpPassword { get; set; } = string.Empty;

    /// <summary>Gets or sets the sender address.</summary>
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>Gets or sets the sender display name.</summary>
    public string FromName { get; set; } = "Jellyfin";

    /// <summary>Gets or sets the PIN validity in minutes.</summary>
    public int PinMinutes { get; set; } = 30;

    /// <summary>Gets or sets the invitation validity in days.</summary>
    public int InvitationDays { get; set; } = 7;

    /// <summary>Gets or sets the e-mail change confirmation validity in hours.</summary>
    public int ConfirmationHours { get; set; } = 48;

    /// <summary>Gets or sets the validity in hours of the link cancelling an e-mail change.</summary>
    public int CancelEmailChangeHours { get; set; } = 24;

    /// <summary>
    /// Gets or sets the former setting, in days, kept only to read configurations saved by earlier versions: it is never
    /// written back. The old default (7 days) is dropped in favour of the new default; any other value is converted to hours.
    /// </summary>
    [XmlElement("CancelEmailChangeDays")]
    public int? LegacyCancelEmailChangeDays
    {
        get => null;
        set
        {
            if (value is int days and >= 1 and <= 4000 and not 7)
            {
                CancelEmailChangeHours = days * 24;
            }
        }
    }

    /// <summary>Gets or sets the recovery e-mails allowed per account per hour. Not shown in the administration page: it protects an account from being flooded.</summary>
    public int MaxRecoveryEmailsPerAccountPerHour { get; set; } = 3;

    /// <summary>Gets or sets the forgot-password requests allowed per IP per 15 minutes.</summary>
    public int MaxForgotRequestsPerIpPer15Min { get; set; } = 5;

    /// <summary>Gets or sets the e-mails one recipient can receive in 24 hours (any kind: recovery, invitation, confirmation). Settings of earlier versions with hourly limits are no longer read.</summary>
    public int MaxEmailsPerRecipientPerDay { get; set; } = 10;

    /// <summary>Gets or sets the e-mails the server sends in 24 hours, all recipients together. The test e-mail and the notices of an e-mail change are not counted.</summary>
    public int MaxEmailsPerDay { get; set; } = 200;

    /// <summary>Gets or sets the address told, by e-mail, when a daily limit is reached; empty sends nothing.</summary>
    public string AdminNotificationAddress { get; set; } = string.Empty;

    /// <summary>Gets or sets the PIN failures after which the PIN is invalidated.</summary>
    public int MaxPinFailures { get; set; } = 5;

    /// <summary>Gets or sets a value indicating whether administrators are handled.</summary>
    public bool AllowAdministrators { get; set; }

    /// <summary>Gets or sets which sessions are closed after a reset.</summary>
    public SessionClosePolicy SessionsOnReset { get; set; } = SessionClosePolicy.All;

    /// <summary>Gets or sets the inactivity threshold in days for <see cref="SessionClosePolicy.InactiveSinceDays"/>.</summary>
    public int SessionInactiveDays { get; set; } = 30;

    /// <summary>Gets or sets how the logo of the e-mails is delivered.</summary>
    public EmailLogoMode EmailLogo { get; set; } = EmailLogoMode.Embedded;

    /// <summary>Gets or sets a value indicating whether the Seerr integration is enabled.</summary>
    public bool SeerrEnabled { get; set; }

    /// <summary>Gets or sets the Seerr base URL.</summary>
    public string SeerrUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the Seerr API key.</summary>
    public string SeerrApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets the template overrides, keyed <c>{lang}/{template}.{html|txt}</c>.
    /// Not serialized directly (XmlSerializer cannot handle dictionaries); see <see cref="TemplateOverrideEntries"/>.
    /// </summary>
    [XmlIgnore]
    public Dictionary<string, string> TemplateOverrides { get; } = new();

    /// <summary>
    /// Gets or sets the XML representation of <see cref="TemplateOverrides"/>.
    /// </summary>
#pragma warning disable CA1819 // Properties should not return arrays: required by XmlSerializer.
    [XmlArray("TemplateOverrides")]
    [XmlArrayItem("Entry")]
    public TemplateOverrideEntry[] TemplateOverrideEntries
    {
        get => TemplateOverrides.Select(kv => new TemplateOverrideEntry { Key = kv.Key, Value = kv.Value }).ToArray();
        set
        {
            TemplateOverrides.Clear();
            foreach (var entry in value ?? Array.Empty<TemplateOverrideEntry>())
            {
                TemplateOverrides[entry.Key] = entry.Value;
            }
        }
    }
#pragma warning restore CA1819

    /// <summary>Tells the XML serializer never to write <see cref="LegacyCancelEmailChangeDays"/>.</summary>
    /// <returns>False.</returns>
#pragma warning disable CA1822 // XmlSerializer looks for this method on the instance.
    public bool ShouldSerializeLegacyCancelEmailChangeDays() => false;
#pragma warning restore CA1822

    /// <summary>
    /// Returns whether e-mails can be sent: the SMTP settings are complete (see <see cref="SmtpSettings.IsComplete"/>).
    /// </summary>
    /// <returns>True when e-mail sending is configured.</returns>
    public bool CanSendEmail() => SmtpSettings.IsComplete(this);
}
