using System;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Email;
using MediaBrowser.Model.Users;

namespace Jellyfin.Plugin.MailConcierge.Web;

/// <summary>
/// Builds the single response returned to every forgot-password request, whatever the state of the account.
/// </summary>
public sealed class ForgotPasswordResponses
{
    private readonly Func<PluginConfiguration> _config;
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="ForgotPasswordResponses"/> class.
    /// </summary>
    /// <param name="config">Accessor of the current configuration.</param>
    /// <param name="time">The time source.</param>
    public ForgotPasswordResponses(Func<PluginConfiguration> config, TimeProvider time)
    {
        _config = config;
        _time = time;
    }

    /// <summary>
    /// Gets the neutral result: a PIN-code action with a generic sentence and the PIN lifetime, rounded down to the minute.
    /// </summary>
    /// <param name="language">The language of the sentence.</param>
    /// <returns>The neutral result.</returns>
    public ForgotPasswordResult Neutral(string language)
    {
        var expires = _time.GetUtcNow().UtcDateTime.AddMinutes(_config().PinMinutes);
        return new ForgotPasswordResult
        {
            Action = ForgotPasswordAction.PinCode,
            PinFile = EmailTemplates.GetString(language, "forgot.neutral"),
            PinExpirationDate = new DateTime(expires.Year, expires.Month, expires.Day, expires.Hour, expires.Minute, 0, DateTimeKind.Utc),
        };
    }
}
