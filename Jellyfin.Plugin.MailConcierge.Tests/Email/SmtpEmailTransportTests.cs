using System;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Email;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MimeKit;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Email;

public class SmtpEmailTransportTests
{
    [Fact]
    public void Build_ProducesMultipartAlternative_WithMessageIdOnSenderDomain()
    {
        var config = new PluginConfiguration { FromAddress = "jf@mail.example.org", FromName = "Home" };
        var message = new EmailMessage("bob@example.com", "Hi", "<p>Hi</p>", "Hi", "recovery", null);

        var mime = SmtpEmailTransport.Build(message, config);

        Assert.EndsWith("@mail.example.org", mime.MessageId, StringComparison.Ordinal);
        var body = Assert.IsType<MultipartAlternative>(mime.Body);
        Assert.Equal("text/plain", body[0].ContentType.MimeType);
        Assert.Equal("text/html", body[1].ContentType.MimeType);
        Assert.Equal("jf@mail.example.org", ((MailboxAddress)mime.From[0]).Address);
        Assert.Equal("bob@example.com", ((MailboxAddress)mime.To[0]).Address);
    }

    [Fact]
    public async Task DisconnectQuietlyAsync_IgnoresDisconnectErrors()
    {
        var client = new Mock<ISmtpClient>();
        client.Setup(c => c.DisconnectAsync(true, It.IsAny<CancellationToken>())).ThrowsAsync(new System.IO.IOException("connection reset"));

        await SmtpEmailTransport.DisconnectQuietlyAsync(client.Object);

        client.Verify(c => c.DisconnectAsync(true, It.IsAny<CancellationToken>()), Times.Once);
    }
}
