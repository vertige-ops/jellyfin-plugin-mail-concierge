using System.IO;
using System.Xml.Serialization;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Configuration;

public class PluginConfigurationTests
{
    [Fact]
    public void Defaults_MatchSpec()
    {
        var c = new PluginConfiguration();

        Assert.True(c.Enabled);
        Assert.False(c.NativeForgotPassword);
        Assert.Equal(string.Empty, c.PublicUrl);
        Assert.Equal("en", c.DefaultLanguage);

        Assert.Equal(string.Empty, c.SmtpHost);
        Assert.Equal(587, c.SmtpPort);
        Assert.Equal(SmtpSecurity.StartTls, c.SmtpSecurity);
        Assert.Equal(string.Empty, c.SmtpUsername);
        Assert.Equal(string.Empty, c.SmtpPassword);
        Assert.Equal(string.Empty, c.FromAddress);
        Assert.Equal("Jellyfin", c.FromName);

        Assert.Equal(30, c.PinMinutes);
        Assert.Equal(7, c.InvitationDays);
        Assert.Equal(48, c.ConfirmationHours);
        Assert.Equal(24, c.CancelEmailChangeHours);

        Assert.Equal(3, c.MaxRecoveryEmailsPerAccountPerHour);
        Assert.Equal(5, c.MaxForgotRequestsPerIpPer15Min);
        Assert.Equal(10, c.MaxRecoveryEmailsPerHour);
        Assert.Equal(10, c.MaxInvitationEmailsPerHour);
        Assert.Equal(10, c.MaxAccountEmailsPerHour);
        Assert.Equal(5, c.MaxPinFailures);

        Assert.False(c.AllowAdministrators);
        Assert.Equal(SessionClosePolicy.All, c.SessionsOnReset);
        Assert.Equal(30, c.SessionInactiveDays);

        Assert.False(c.SeerrEnabled);
        Assert.Equal(string.Empty, c.SeerrUrl);
        Assert.Equal(string.Empty, c.SeerrApiKey);

        Assert.NotNull(c.TemplateOverrides);
        Assert.Empty(c.TemplateOverrides);
    }

    [Fact]
    public void CanSendEmail_FalseWhenPublicUrlEmpty()
    {
        var c = new PluginConfiguration { SmtpHost = "smtp.example.org", FromAddress = "jf@example.org" };
        Assert.False(c.CanSendEmail());

        c.PublicUrl = "https://jf.example.org";
        Assert.True(c.CanSendEmail());

        c.SmtpHost = string.Empty;
        Assert.False(c.CanSendEmail());

        c.SmtpHost = "smtp.example.org";
        c.FromAddress = " ";
        Assert.False(c.CanSendEmail());
    }

    [Fact]
    public void Config_RoundTripsThroughXmlSerializer()
    {
        var c = new PluginConfiguration
        {
            PublicUrl = "https://jf.example.org",
            SmtpPort = 465,
            SmtpSecurity = SmtpSecurity.SslOnConnect,
            SessionsOnReset = SessionClosePolicy.InactiveSinceDays,
        };
        c.TemplateOverrides["fr/invite.html"] = "<p>Bonjour</p>";
        c.TemplateOverrides["en/reset.txt"] = "Hello";

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, c);
        using var reader = new StringReader(writer.ToString());
        var back = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        Assert.Equal("https://jf.example.org", back.PublicUrl);
        Assert.Equal(465, back.SmtpPort);
        Assert.Equal(SmtpSecurity.SslOnConnect, back.SmtpSecurity);
        Assert.Equal(SessionClosePolicy.InactiveSinceDays, back.SessionsOnReset);
        Assert.Equal(2, back.TemplateOverrides.Count);
        Assert.Equal("<p>Bonjour</p>", back.TemplateOverrides["fr/invite.html"]);
        Assert.Equal("Hello", back.TemplateOverrides["en/reset.txt"]);
    }

    [Theory]
    [InlineData("<CancelEmailChangeDays>7</CancelEmailChangeDays>", 24)]
    [InlineData("<CancelEmailChangeDays>3</CancelEmailChangeDays>", 72)]
    [InlineData("<CancelEmailChangeDays>0</CancelEmailChangeDays>", 24)]
    [InlineData("<CancelEmailChangeDays>2</CancelEmailChangeDays><CancelEmailChangeHours>6</CancelEmailChangeHours>", 6)]
    [InlineData("<CancelEmailChangeHours>12</CancelEmailChangeHours>", 12)]
    [InlineData("", 24)]
    public void Config_ReadsTheLegacyCancelDaysSetting_WithoutError(string element, int expectedHours)
    {
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><PluginConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" + element + "</PluginConfiguration>";
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader(xml);
        var back = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        Assert.Equal(expectedHours, back.CancelEmailChangeHours);
    }

    [Fact]
    public void Config_DoesNotWriteTheLegacySettingBack()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, new PluginConfiguration());

        Assert.Contains("<CancelEmailChangeHours>24</CancelEmailChangeHours>", writer.ToString(), System.StringComparison.Ordinal);
        Assert.DoesNotContain("CancelEmailChangeDays", writer.ToString(), System.StringComparison.Ordinal);
    }
}
