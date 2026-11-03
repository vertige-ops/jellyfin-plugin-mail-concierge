using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MailConcierge.Email;
using Xunit;

namespace Jellyfin.Plugin.MailConcierge.Tests.Email;

public class EmailTemplatesTests
{
    private static readonly Dictionary<string, string> NoOverrides = new();

    [Theory]
    [InlineData("recovery")]
    [InlineData("invitation")]
    [InlineData("confirm-email")]
    [InlineData("email-changed")]
    [InlineData("email-change-cancelled")]
    [InlineData("password-changed")]
    public void Render_ShowsTheLogoAtTheTop_InEveryLanguage(string template)
    {
        foreach (var lang in new[] { "en", "fr", "es", "de" })
        {
            var values = new Dictionary<string, string> { ["serverName"] = "Home", ["logoUrl"] = "https://media.example/MailConcierge/assets/logo.png" };

            var (_, html, _) = EmailTemplates.Render(template, lang, values, NoOverrides);

            Assert.Contains("<img src=\"https://media.example/MailConcierge/assets/logo.png\" width=\"56\" height=\"56\" alt=\"\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("{{logoRow}}", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Render_WithoutLogoUrl_HasNoImage()
    {
        var values = new Dictionary<string, string> { ["serverName"] = "Home", ["logoUrl"] = string.Empty };

        var (_, html, _) = EmailTemplates.Render("password-changed", "en", values, NoOverrides);

        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.DoesNotContain("logoRow", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://media.example", "https://media.example/MailConcierge/assets/logo.png")]
    [InlineData(" https://media.example/jf/ ", "https://media.example/jf/MailConcierge/assets/logo.png")]
    [InlineData("", null)]
    [InlineData("media.example", null)]
    [InlineData("javascript:alert(1)", null)]
    public void LogoUrl_IsBuiltFromThePublicUrl(string publicUrl, string? expected)
    {
        Assert.Equal(expected, EmailTemplates.LogoUrl(publicUrl));
    }

    [Theory]
    [InlineData("https://media.example/{{pin}}")]
    [InlineData("https://media.example/a\"onerror=\"x")]
    [InlineData("https://media.example/<script>")]
    [InlineData("https://media.example/a b")]
    public void LogoUrl_RefusesUrlsThatCouldBreakTheMarkup(string publicUrl)
    {
        Assert.Null(EmailTemplates.LogoUrl(publicUrl));
    }

    [Fact]
    public void Render_LogoUrlIsNeverExpandedAsAPlaceholder()
    {
        var values = new Dictionary<string, string> { ["serverName"] = "Home", ["pin"] = "123456", ["logoUrl"] = "https://media.example/{{pin}}" };

        var (_, html, _) = EmailTemplates.Render("recovery", "en", values, NoOverrides);

        Assert.DoesNotContain("media.example/123456", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_OverrideWithoutTheMarkerHasNoLogo_AndTheMarkerTextInAValueIsInert()
    {
        var overrides = new Dictionary<string, string>
        {
            ["en/password-changed.txt"] = "Subject: S\n\nBody",
            ["en/password-changed.html"] = "<p>Hi {{username}}</p>",
        };
        var values = new Dictionary<string, string> { ["username"] = "{{logoRow}}", ["logoUrl"] = "https://media.example/MailConcierge/assets/logo.png" };

        var (_, html, _) = EmailTemplates.Render("password-changed", "en", values, overrides);

        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.Contains("{{logoRow}}", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_HtmlEncodesValues()
    {
        var values = new Dictionary<string, string>
        {
            ["username"] = "<b>Eve</b>",
            ["serverName"] = "Home",
            ["link"] = "https://x.example/r?a=1&b=2",
            ["pin"] = "123456",
            ["expires"] = "tomorrow",
        };

        var (_, html, text) = EmailTemplates.Render("recovery", "en", values, NoOverrides);

        Assert.Contains("&lt;b&gt;Eve&lt;/b&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>Eve</b>", html, StringComparison.Ordinal);
        Assert.Contains("<b>Eve</b>", text, StringComparison.Ordinal);
        Assert.Contains("a=1&amp;b=2", html, StringComparison.Ordinal);
        Assert.Contains("a=1&b=2", text, StringComparison.Ordinal);
        Assert.Contains("123456", html, StringComparison.Ordinal);
        Assert.Contains("123456", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_FallsBackToEnglish()
    {
        var values = new Dictionary<string, string> { ["serverName"] = "Home" };

        var english = EmailTemplates.Render("password-changed", "en", values, NoOverrides);
        var unsupported = EmailTemplates.Render("password-changed", "ja", values, NoOverrides);

        Assert.Equal(english.Subject, unsupported.Subject);
        Assert.NotEqual(english.Subject, EmailTemplates.Render("password-changed", "fr", values, NoOverrides).Subject);
    }

    [Fact]
    public void Render_UsesOverrideWhenPresent()
    {
        var overrides = new Dictionary<string, string>
        {
            ["en/password-changed.txt"] = "Subject: Custom {{serverName}}\n\nHi <x>{{username}}",
            ["en/password-changed.html"] = "<p>Custom {{username}}</p>",
        };
        var values = new Dictionary<string, string> { ["serverName"] = "Home", ["username"] = "<x>" };

        var (subject, html, text) = EmailTemplates.Render("password-changed", "en", values, overrides);

        Assert.Equal("Custom Home", subject);
        Assert.Equal("<p>Custom &lt;x&gt;</p>", html);
        Assert.Equal("Hi <x><x>", text);
    }

    [Fact]
    public void Render_SubjectCannotCarryLineBreaks()
    {
        var values = new Dictionary<string, string> { ["serverName"] = "Home\r\nBcc: evil@example.com" };

        var (subject, _, _) = EmailTemplates.Render("invitation", "en", values, NoOverrides);

        Assert.DoesNotContain('\n', subject);
        Assert.DoesNotContain('\r', subject);
    }

    [Fact]
    public void AllTemplates_ExistInEnAndFr_WithSamePlaceholders()
    {
        foreach (var template in EmailTemplates.TemplateNames)
        {
            foreach (var ext in new[] { "html", "txt" })
            {
                var en = EmailTemplates.ReadEmbedded("en", template, ext);
                var fr = EmailTemplates.ReadEmbedded("fr", template, ext);
                Assert.False(string.IsNullOrWhiteSpace(en), $"en/{template}.{ext} missing");
                Assert.False(string.IsNullOrWhiteSpace(fr), $"fr/{template}.{ext} missing");
                Assert.Equal(Placeholders(en!), Placeholders(fr!));
            }

            Assert.Equal(Placeholders(EmailTemplates.ReadEmbedded("en", template, "html")!), Placeholders(EmailTemplates.ReadEmbedded("en", template, "txt")!));
            Assert.StartsWith("Subject: ", EmailTemplates.ReadEmbedded("fr", template, "txt"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Strings_ExistInEnAndFr_WithSameKeys()
    {
        var en = EmailTemplates.GetString("en", "email.test.subject");
        var fr = EmailTemplates.GetString("fr", "email.test.subject");
        Assert.NotEqual(en, fr);
    }

    private static string[] Placeholders(string content)
        => Regex.Matches(content, @"\{\{(\w+)\}\}").Select(m => m.Groups[1].Value).Where(n => n != "logoRow").Distinct().OrderBy(s => s, System.StringComparer.Ordinal).ToArray();
}
