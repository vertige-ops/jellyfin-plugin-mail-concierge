using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MailConcierge.Email;

/// <summary>
/// Sends one e-mail.
/// </summary>
public interface IEmailTransport
{
    /// <summary>
    /// Sends a message. Throws when the message could not be delivered to the server.
    /// </summary>
    /// <param name="m">The message.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task completing when the message was accepted.</returns>
    Task SendAsync(EmailMessage m, CancellationToken ct);
}
