using System;
using System.Globalization;
using System.IO;
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

        await client.ConnectAsync(ConnectHost(config.SmtpHost), config.SmtpPort, security, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(config.SmtpUsername))
        {
            await client.AuthenticateAsync(config.SmtpUsername, config.SmtpPassword, ct).ConfigureAwait(false);
        }

        await client.SendAsync(mime, ct).ConfigureAwait(false);
        await DisconnectQuietlyAsync(client).ConfigureAwait(false);
    }

    /// <summary>
    /// The host as the SMTP client wants it: an IPv6 address is accepted in the settings with or without brackets, but only the bare
    /// form can be connected to. One leading <c>[</c> and one trailing <c>]</c> are removed, and only when both are there.
    /// </summary>
    /// <param name="host">The stored host (never changed in the settings).</param>
    /// <returns>The host to connect to.</returns>
    internal static string ConnectHost(string host)
        => host is { Length: > 2 } && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;

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
        var alternative = new MultipartAlternative
        {
            new TextPart("plain") { Text = m.Text },
            new TextPart("html") { Text = m.Html },
        };

        // The inline logo travels with the message exactly when its HTML refers to it, whatever the setting is now.
        // The usual layout is related(alternative(text, html), image): Gmail then treats the image as part of the
        // message body instead of listing it as an attachment.
        if (m.Html.Contains("cid:" + EmailTemplates.LogoContentId, StringComparison.Ordinal)
            && EmailTemplates.LogoBytes() is { } logo)
        {
            var part = new MimePart("image", "png")
            {
                Content = new MimeContent(new MemoryStream(logo)),
                ContentTransferEncoding = ContentEncoding.Base64,
                ContentId = EmailTemplates.LogoContentId,
                ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
            };
            mime.Body = new MultipartRelated { alternative, part };
        }
        else
        {
            mime.Body = alternative;
        }

        return mime;
    }
}
