using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MailConcierge.Configuration;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Jellyfin.Plugin.MailConcierge.Email;

/// <summary>
/// Sends e-mails through the SMTP server of the plugin configuration, using MailKit.
/// </summary>
public sealed class SmtpEmailTransport : IEmailTransport
{
    private static readonly int TimeoutMs = (int)TimeSpan.FromSeconds(30).TotalMilliseconds;

    private readonly Func<PluginConfiguration> _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="SmtpEmailTransport"/> class.
    /// </summary>
    /// <param name="config">Accessor of the current configuration.</param>
    public SmtpEmailTransport(Func<PluginConfiguration> config)
    {
        _config = config;
    }

    /// <inheritdoc />
    public async Task SendAsync(EmailMessage m, CancellationToken ct)
    {
        var config = _config();
        var mime = Build(m, config);

        using var client = new SmtpClient { Timeout = TimeoutMs };
        var security = config.SmtpSecurity switch
        {
            SmtpSecurity.None => SecureSocketOptions.None,
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.StartTls,
        };

        await client.ConnectAsync(config.SmtpHost, config.SmtpPort, security, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(config.SmtpUsername))
        {
            await client.AuthenticateAsync(config.SmtpUsername, config.SmtpPassword, ct).ConfigureAwait(false);
        }

        await client.SendAsync(mime, ct).ConfigureAwait(false);
        await DisconnectQuietlyAsync(client).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the connection once the message has been accepted. A failure here is ignored: the message was sent, and
    /// reporting an error would make the queue send it again.
    /// </summary>
    /// <param name="client">The SMTP client.</param>
    /// <returns>A task that never faults.</returns>
    internal static async Task DisconnectQuietlyAsync(ISmtpClient client)
    {
        try
        {
            await client.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The server accepted the message; the connection is disposed by the caller anyway.
        }
    }

    /// <summary>
    /// Builds the MIME message: multipart/alternative (text, then HTML) with a Message-ID on the sender domain.
    /// </summary>
    /// <param name="m">The message.</param>
    /// <param name="config">The configuration.</param>
    /// <returns>The MIME message.</returns>
    internal static MimeMessage Build(EmailMessage m, PluginConfiguration config)
    {
        var from = MailboxAddress.Parse(config.FromAddress);
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(config.FromName, from.Address));
        mime.To.Add(MailboxAddress.Parse(m.To));
        mime.Subject = m.Subject;
        mime.Date = DateTimeOffset.UtcNow;
        mime.MessageId = string.Create(CultureInfo.InvariantCulture, $"{Guid.NewGuid():N}@{from.Domain}");
        mime.Headers.Add("Auto-Submitted", "auto-generated");
        mime.Body = new BodyBuilder { TextBody = m.Text, HtmlBody = m.Html }.ToMessageBody();
        return mime;
    }
}
