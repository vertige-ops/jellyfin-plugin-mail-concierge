namespace Jellyfin.Plugin.MailConcierge.Configuration;

/// <summary>
/// How the logo shown at the top of the e-mails reaches the reader.
/// </summary>
public enum EmailLogoMode
{
    /// <summary>The logo is loaded from the server when the message is opened.</summary>
    Hosted,

    /// <summary>The logo travels inside the message as an inline attachment.</summary>
    Embedded,

    /// <summary>No logo.</summary>
    None,
}
