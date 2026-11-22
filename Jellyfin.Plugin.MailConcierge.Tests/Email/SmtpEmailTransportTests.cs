using System;
using System.Linq;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Email;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MimeKit;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Email;

[Collection(LogoSeamCollection.Name)]
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
    public void Build_WithTheLogoReference_AddsTheLogoAsALinkedResource()
    {
        var config = new PluginConfiguration { FromAddress = "jf@mail.example.org", FromName = "Home" };
        var html = "<p><img src=\"cid:mailconcierge-logo@mailconcierge.invalid\"></p>";
        var message = new EmailMessage("bob@example.com", "Hi", html, "Hi", "recovery", null);

        var mime = SmtpEmailTransport.Build(message, config);

        var related = Assert.IsType<MultipartRelated>(mime.Body);
        var alternative = Assert.IsType<MultipartAlternative>(related[0]);
        Assert.Equal("text/plain", alternative[0].ContentType.MimeType);
        Assert.Equal("text/html", alternative[1].ContentType.MimeType);
        var logo = Assert.IsType<MimePart>(related[1]);
        Assert.Null(logo.ContentLocation);
        Assert.Equal("image/png", logo.ContentType.MimeType);
        Assert.Equal("mailconcierge-logo@mailconcierge.invalid", logo.ContentId);
        Assert.Equal(EmailTemplates.LogoBytes()?.Length, ReadAll(logo).Length);
        Assert.Equal("inline", logo.ContentDisposition?.Disposition);
        Assert.Null(logo.ContentDisposition?.FileName);
        Assert.Null(logo.ContentType.Name);
    }

    [Fact]
    public void Build_WithoutTheLogoReference_HasNoLinkedResource_EvenWhenTheSettingIsEmbedded()
    {
        var config = new PluginConfiguration { FromAddress = "jf@mail.example.org", FromName = "Home", EmailLogo = EmailLogoMode.Embedded };
        var html = "<p><img src=\"https://media.example/MailConcierge/assets/logo.png\"></p>";
        var message = new EmailMessage("bob@example.com", "Hi", html, "Hi", "recovery", null);

        var mime = SmtpEmailTransport.Build(message, config);

        var alternative = Assert.IsType<MultipartAlternative>(mime.Body);
        Assert.IsNotType<MultipartRelated>(mime.Body);
        Assert.Equal("text/html", alternative[1].ContentType.MimeType);
        Assert.DoesNotContain(mime.BodyParts.OfType<MimePart>(), p => p.ContentId is not null);
    }

    [Fact]
    public void Build_WithTheLogoReferenceButAnUnreadableResource_BuildsTheMessageWithoutALinkedResource()
    {
        using var seam = LogoSeam.Unreadable();
        var config = new PluginConfiguration { FromAddress = "jf@mail.example.org", FromName = "Home" };
        var html = "<p><img src=\"cid:mailconcierge-logo@mailconcierge.invalid\"></p>";
        var message = new EmailMessage("bob@example.com", "Hi", html, "Hi", "recovery", null);

        var mime = SmtpEmailTransport.Build(message, config);

        var alternative = Assert.IsType<MultipartAlternative>(mime.Body);
        Assert.Equal("text/html", alternative[1].ContentType.MimeType);
        Assert.IsNotType<MultipartRelated>(mime.Body);
    }

    private static byte[] ReadAll(MimePart part)
    {
        using var ms = new System.IO.MemoryStream();
        part.Content!.DecodeTo(ms);
        return ms.ToArray();
    }

    [Theory]
    [InlineData("[2001:db8::1]", "2001:db8::1")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("smtp.example.org", "smtp.example.org")]
    [InlineData("[", "[")]
    [InlineData("]", "]")]
    [InlineData("[2001:db8::1", "[2001:db8::1")]
    [InlineData("2001:db8::1]", "2001:db8::1]")]
    [InlineData("[]", "[]")]
    [InlineData("[[2001:db8::1]]", "[2001:db8::1]")]
    [InlineData("", "")]
    public void ConnectHost_StripsOneBracketPair_OnlyWhenBalanced(string stored, string expected)
        => Assert.Equal(expected, SmtpEmailTransport.ConnectHost(stored));

    [Fact]
    public async Task DisconnectQuietlyAsync_IgnoresDisconnectErrors()
    {
        var client = new Mock<ISmtpClient>();
        client.Setup(c => c.DisconnectAsync(true, It.IsAny<CancellationToken>())).ThrowsAsync(new System.IO.IOException("connection reset"));

        await SmtpEmailTransport.DisconnectQuietlyAsync(client.Object);

        client.Verify(c => c.DisconnectAsync(true, It.IsAny<CancellationToken>()), Times.Once);
    }
}
