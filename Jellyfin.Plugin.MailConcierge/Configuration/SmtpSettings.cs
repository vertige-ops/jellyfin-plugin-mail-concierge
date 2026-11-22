using System;
using System.Linq;
using Jellyfin.Plugin.MailConcierge.Accounts;

namespace Jellyfin.Plugin.MailConcierge.Configuration;

/// <summary>
/// The rules that decide whether the SMTP settings can be used to send e-mail.
/// </summary>
public static class SmtpSettings
{
    private const int MaxHostChars = 253;

    /// <summary>
    /// Tells whether a text is a host name or an IP address, and nothing else (no scheme, port, path, user or space).
    /// </summary>
    /// <param name="host">The text to check.</param>
    /// <returns>True when it can be used as an SMTP host.</returns>
    public static bool IsValidHost(string? host)
        => !string.IsNullOrEmpty(host)
           && host.Length <= MaxHostChars
           && !host.Any(char.IsWhiteSpace)
           && Uri.CheckHostName(host) != UriHostNameType.Unknown;

    /// <summary>
    /// Tells whether the settings are complete: a public URL, a valid host, a port from 1 to 65535 and a valid sender address.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>True when e-mail can be sent.</returns>
    public static bool IsComplete(PluginConfiguration config)
        => !string.IsNullOrWhiteSpace(config.PublicUrl)
           && IsValidHost(config.SmtpHost)
           && config.SmtpPort is >= 1 and <= 65535
           && EmailService.TryNormalize(config.FromAddress, out _);
}
