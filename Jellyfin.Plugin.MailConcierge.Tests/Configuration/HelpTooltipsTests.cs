using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MailConcierge.Localization;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Configuration;

public class HelpTooltipsTests
{
    private static readonly System.Reflection.Assembly Asm = typeof(Languages).Assembly;

    private static string ReadResource(string name)
    {
        using var stream = Asm.GetManifestResourceStream(name) ?? throw new InvalidOperationException("Missing resource " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static List<string> HelpKeys()
    {
        var html = ReadResource("Jellyfin.Plugin.MailConcierge.Configuration.configPage.html");
        return Regex.Matches(html, "<span\\b[^>]*\\bdata-help=\"([^\"]*)\"[^>]*>")
            .Select(m => m.Groups[1].Value)
            .ToList();
    }

    [Fact]
    public void ConfigPage_HasTheHelpIconsOfTheComplexFields()
    {
        var keys = HelpKeys();
        string[] expected =
        {
            "help.publicUrl", "help.defaultLanguage", "help.allowAdministrators", "help.emailLogin", "help.sessionsOnReset", "help.sessionDays",
            "help.smtpSecurity", "help.fromAddress", "help.invitationDays", "help.pinMinutes", "help.confirmationHours",
            "help.cancelHours", "help.maxRecoveryHour", "help.maxInvitationHour", "help.maxAccountHour", "help.maxAccount", "help.maxIp", "help.maxPin", "help.templates",
            "help.seerrUrl", "help.seerrApiKey", "help.assignProvider",
        };
        foreach (var key in expected)
        {
            Assert.Contains(key, keys);
        }

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ConfigPage_Styles_AreInsideThePageElement()
    {
        // Jellyfin keeps only the data-role="page" element of a plugin page: styles outside it are dropped.
        var html = ReadResource("Jellyfin.Plugin.MailConcierge.Configuration.configPage.html");
        var page = html.IndexOf("data-role=\"page\"", StringComparison.Ordinal);
        var style = html.IndexOf("<style", StringComparison.Ordinal);
        Assert.True(page >= 0 && style > page, "The <style> element must be inside the data-role=\"page\" element.");
    }

    [Fact]
    public void ConfigPage_FieldHelpIcons_SitNextToTheirLabelOutsideIt()
    {
        // Same row as the label (not at the end of the line), and never inside the label: a checkbox label would swallow the hover.
        var html = ReadResource("Jellyfin.Plugin.MailConcierge.Configuration.configPage.html");
        var icons = Regex.Matches(html, "<span class=\"ucHelp ucHelpField\"[^>]*></span>").Count;
        var rows = Regex.Matches(html, "<div class=\"ucHelpRow\"><label\\b[^>]*>.*?</label><span class=\"ucHelp ucHelpField\"[^>]*></span></div>").Count;
        Assert.True(icons > 0);
        Assert.Equal(icons, rows);
    }

    [Fact]
    public void ConfigPage_HelpIcons_AreFocusableAndLabelled()
    {
        var html = ReadResource("Jellyfin.Plugin.MailConcierge.Configuration.configPage.html");
        foreach (Match m in Regex.Matches(html, "<span\\b[^>]*\\bdata-help=[^>]*>"))
        {
            Assert.Contains("tabindex=\"0\"", m.Value, StringComparison.Ordinal);
            Assert.Contains("role=\"img\"", m.Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ConfigPage_HelpIcons_AreNotInsideLabels_NorHaveNativeTitle()
    {
        // Inside a label the help sentence would become part of the field's accessible name.
        var html = ReadResource("Jellyfin.Plugin.MailConcierge.Configuration.configPage.html");
        foreach (Match label in Regex.Matches(html, "<label\\b.*?</label>", RegexOptions.Singleline))
        {
            Assert.DoesNotContain("data-help", label.Value, StringComparison.Ordinal);
        }

        foreach (Match m in Regex.Matches(html, "<span\\b[^>]*\\bdata-help=[^>]*>"))
        {
            Assert.DoesNotContain("title=", m.Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryHelpKey_ExistsNonEmpty_InEveryLanguage()
    {
        var keys = HelpKeys();
        Assert.NotEmpty(keys);
        foreach (var language in Languages.Supported)
        {
            using var doc = JsonDocument.Parse(ReadResource($"Jellyfin.Plugin.MailConcierge.Resources.Strings.{language.Code}.json"));
            foreach (var key in keys)
            {
                Assert.True(
                    doc.RootElement.TryGetProperty("cfg." + key, out var value) && !string.IsNullOrWhiteSpace(value.GetString()),
                    $"cfg.{key} missing or empty in {language.Code}.json");
            }
        }
    }
}
