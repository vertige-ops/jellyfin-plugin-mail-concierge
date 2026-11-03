namespace Jellyfin.Plugin.MailConcierge.Configuration;

/// <summary>
/// Which sessions are closed after a password reset.
/// </summary>
public enum SessionClosePolicy
{
    /// <summary>Keep every session.</summary>
    None,

    /// <summary>Close every session of the user.</summary>
    All,

    /// <summary>Close only sessions inactive for <see cref="PluginConfiguration.SessionInactiveDays"/> days or more.</summary>
    InactiveSinceDays,
}
