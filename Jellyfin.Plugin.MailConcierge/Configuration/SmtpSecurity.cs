namespace Jellyfin.Plugin.MailConcierge.Configuration;

/// <summary>
/// Transport security used for the SMTP connection.
/// </summary>
public enum SmtpSecurity
{
    /// <summary>No encryption.</summary>
    None,

    /// <summary>Upgrade the connection with STARTTLS.</summary>
    StartTls,

    /// <summary>TLS from the first byte (implicit TLS).</summary>
    SslOnConnect,
}
