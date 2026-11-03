using System;
using System.Linq;
using Jellyfin.Plugin.MailConcierge.Email;
using Jellyfin.Plugin.MailConcierge.Localization;
using MediaBrowser.Controller;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Localization;

public class LanguagesTests
{
    [Fact]
    public void Supported_IsDiscoveredFromTheStringsFiles_WithOwnNames()
    {
        var supported = Languages.Supported.ToDictionary(l => l.Code, l => l.Name);

        Assert.Equal("English", supported["en"]);
        Assert.Equal("Français", supported["fr"]);
        Assert.Equal("Español", supported["es"]);
        Assert.Equal("Deutsch", supported["de"]);
        Assert.Equal(supported.Keys.OrderBy(c => c, StringComparer.Ordinal), supported.Keys);
    }

    [Fact]
    public void EverySupportedLanguage_HasAllTheEmailTemplates()
    {
        // Adding Resources/Strings/xx.json without the templates would break the e-mails: caught here.
        foreach (var language in Languages.Supported)
        {
            foreach (var name in EmailTemplates.TemplateNames)
            {
                Assert.NotNull(EmailTemplates.ReadEmbedded(language.Code, name, "html"));
                Assert.NotNull(EmailTemplates.ReadEmbedded(language.Code, name, "txt"));
            }
        }
    }

    [Theory]
    [InlineData("fr", "en", "fr")]
    [InlineData("FR-ca", "en", "fr")]
    [InlineData("en_GB", "fr", "en")]
    [InlineData("xx", "fr", "fr")]
    [InlineData(null, "fr", "fr")]
    [InlineData("", " FR ", "fr")]
    [InlineData("xx", "yy", "en")]
    [InlineData(null, null, "en")]
    public void Normalize_PicksRequested_ThenDefault_ThenEnglish(string? language, string? defaultLanguage, string expected)
        => Assert.Equal(expected, Languages.Normalize(language, defaultLanguage));

    [Theory]
    [InlineData("es", "7 de noviembre de 2026")]
    [InlineData("de", "7. November 2026")]
    [InlineData("fr", "7 novembre 2026")]
    [InlineData("en", "7 November 2026")]
    public void FormatDate_UsesTheNaturalOrderOfEachLanguage(string language, string expected)
        => Assert.Equal(expected, Languages.FormatDate(new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Utc, language));

    [Fact]
    public void FormatDateTime_German_AddsUhr()
        => Assert.Equal("7. November 2026, 12:05 Uhr", Languages.FormatDateTime(new DateTime(2026, 11, 7, 12, 5, 0, DateTimeKind.Utc), TimeZoneInfo.Utc, "de"));

    [Fact]
    public void FormatDate_UnknownCulture_DoesNotThrow()
        => Assert.Equal("7 November 2026", Languages.FormatDate(new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Utc, "zz-invalid-culture"));

    [Theory]
    [InlineData("Pet Cinema", "Pet Cinema")]
    [InlineData("", "Jellyfin")]
    [InlineData("  ", "Jellyfin")]
    public void ServerName_FallsBackToJellyfin_WhenUnnamed(string friendlyName, string expected)
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.FriendlyName).Returns(friendlyName);

        Assert.Equal(expected, Languages.ServerName(host.Object));
    }

    [Fact]
    public void ServerName_MachineNameMeansNoNameWasGiven()
    {
        // Without a configured name Jellyfin reports the machine name, an identifier in a container.
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.FriendlyName).Returns(Environment.MachineName);

        Assert.Equal("Jellyfin", Languages.ServerName(host.Object));
    }
}
