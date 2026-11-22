using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Configuration;

public class SmtpPresetsTests
{
    [Fact]
    public void All_HoldsTheFivePresets_WithUniqueIds()
    {
        var ids = SmtpPresets.All.Select(p => p.Id).ToList();

        Assert.Equal(new[] { "gmail", "brevo", "smtp2go", "mailgun", "mailjet" }, ids);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void All_EveryPresetIsUsable()
    {
        foreach (var p in SmtpPresets.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name), p.Id);
            Assert.True(SmtpSettings.IsValidHost(p.Host), p.Id);
            Assert.InRange(p.Port, 1, 65535);
            Assert.Contains(p.Security, new[] { "starttls", "ssl" });
            Assert.StartsWith("https://", p.DocUrl, StringComparison.Ordinal);
            Assert.True(Uri.TryCreate(p.DocUrl, UriKind.Absolute, out _), p.Id);
            Assert.True(DateOnly.TryParseExact(p.CheckedOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _), p.Id);
        }
    }

    [Fact]
    public void All_HasTheExpectedValues()
    {
        var byId = SmtpPresets.All.ToDictionary(p => p.Id, StringComparer.Ordinal);

        Assert.Equal(("Gmail", "smtp.gmail.com", 587, "starttls"), (byId["gmail"].Name, byId["gmail"].Host, byId["gmail"].Port, byId["gmail"].Security));
        Assert.Equal(("Brevo", "smtp-relay.brevo.com", 587, "starttls"), (byId["brevo"].Name, byId["brevo"].Host, byId["brevo"].Port, byId["brevo"].Security));
        Assert.Equal(("SMTP2GO", "mail.smtp2go.com", 587, "starttls"), (byId["smtp2go"].Name, byId["smtp2go"].Host, byId["smtp2go"].Port, byId["smtp2go"].Security));
        Assert.Equal(("Mailgun", "smtp.eu.mailgun.org", 587, "starttls"), (byId["mailgun"].Name, byId["mailgun"].Host, byId["mailgun"].Port, byId["mailgun"].Security));
        Assert.Equal(("Mailjet", "in-v3.mailjet.com", 587, "starttls"), (byId["mailjet"].Name, byId["mailjet"].Host, byId["mailjet"].Port, byId["mailjet"].Security));
        Assert.All(SmtpPresets.All, p => Assert.Equal("2026-11-15", p.CheckedOn));
        Assert.Equal("https://support.google.com/accounts/answer/185833", byId["gmail"].DocUrl);
        Assert.Equal("https://help.brevo.com/hc/en-us/articles/7924908994450-Send-transactional-emails-using-Brevo-SMTP", byId["brevo"].DocUrl);
        Assert.Equal("https://support.smtp2go.com/hc/en-gb/articles/223087627-SMTP-Settings", byId["smtp2go"].DocUrl);
        Assert.Equal("https://documentation.mailgun.com/docs/mailgun/user-manual/sending-messages/send-smtp", byId["mailgun"].DocUrl);
        Assert.Equal("https://dev.mailjet.com/docs/smtp-relay/configuration", byId["mailjet"].DocUrl);
    }

    [Fact]
    public void TheDataFile_ShipsInsideThePluginAssembly()
    {
        // The loader reads the assembly resource, not a file on disk: this is the same path the plugin uses once installed.
        var resource = typeof(SmtpPresets).Assembly.GetManifestResourceNames()
            .SingleOrDefault(n => n.EndsWith("Resources.Data.smtp-presets.json", StringComparison.Ordinal));

        Assert.NotNull(resource);
        using var stream = typeof(SmtpPresets).Assembly.GetManifestResourceStream(resource!);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var text = reader.ReadToEnd();
        Assert.Contains("smtp.eu.mailgun.org", text, StringComparison.Ordinal);
        Assert.Equal(5, SmtpPresets.All.Count);
    }
}
