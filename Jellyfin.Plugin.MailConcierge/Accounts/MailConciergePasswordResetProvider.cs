using System;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Web;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Model.Users;

namespace Jellyfin.Plugin.MailConcierge.Accounts;

/// <summary>
/// Placeholder password-reset provider so that administrators can see and assign Mail Concierge.
/// The real flow runs through <see cref="RecoveryService"/>; this provider never sends or redeems anything.
/// </summary>
public sealed class MailConciergePasswordResetProvider : IPasswordResetProvider
{
    /// <summary>
    /// The identifier stored in <c>User.PasswordResetProviderId</c>.
    /// </summary>
    public static readonly string Id = typeof(MailConciergePasswordResetProvider).FullName!;

    private readonly ForgotPasswordResponses _responses;
    private readonly Func<PluginConfiguration> _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="MailConciergePasswordResetProvider"/> class.
    /// </summary>
    /// <param name="responses">The neutral response builder.</param>
    /// <param name="config">Accessor of the current configuration.</param>
    public MailConciergePasswordResetProvider(ForgotPasswordResponses responses, Func<PluginConfiguration> config)
    {
        _responses = responses;
        _config = config;
    }

    /// <inheritdoc />
    public string Name => "Mail Concierge (e-mail)";

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public Task<ForgotPasswordResult> StartForgotPasswordProcess(User? user, string enteredUsername, bool isInNetwork)
        => Task.FromResult(_responses.Neutral(_config().DefaultLanguage));

    /// <inheritdoc />
    public Task<PinRedeemResult> RedeemPasswordResetPin(string pin)
        => Task.FromResult(new PinRedeemResult());
}
