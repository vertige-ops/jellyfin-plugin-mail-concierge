namespace Jellyfin.Plugin.MailConcierge.Configuration;

/// <summary>
/// A mail provider's SMTP settings, as shipped in the plugin's data file (read only).
/// </summary>
/// <param name="Id">The stable identifier (lower case), used by the page to find the texts of the provider.</param>
/// <param name="Name">The brand name, not translated.</param>
/// <param name="Host">The SMTP host name.</param>
/// <param name="Port">The SMTP port.</param>
/// <param name="Security">The connection security: <c>starttls</c> or <c>ssl</c>.</param>
/// <param name="DocUrl">The provider's official instructions (https).</param>
/// <param name="CheckedOn">The ISO date (yyyy-MM-dd) of the last check of these values.</param>
public sealed record SmtpPreset(string Id, string Name, string Host, int Port, string Security, string DocUrl, string CheckedOn);
