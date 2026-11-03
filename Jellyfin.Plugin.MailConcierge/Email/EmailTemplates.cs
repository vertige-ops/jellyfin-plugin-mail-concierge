using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MailConcierge.Email;

/// <summary>
/// Renders the e-mail templates (embedded, with optional overrides from the configuration).
/// </summary>
public static class EmailTemplates
{
    private const string SubjectPrefix = "Subject: ";
    private const string FallbackLanguage = "en";

    // Value key that overrides the logo URL (tests); otherwise it comes from the public URL.
    private const string LogoUrlKey = "logoUrl";

    // Marker in the HTML templates replaced by the logo header row.
    private const string LogoRowMarker = "{{logoRow}}";

    // Characters that cannot appear in the public URL used to build the logo address.
    private static readonly SearchValues<char> UnsafeUrlChars = SearchValues.Create("{}<>\"'`\\^|");

    private static readonly Regex Placeholder = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly SearchValues<char> LanguageSeparators = SearchValues.Create("-_");
    private static readonly Assembly Asm = typeof(EmailTemplates).Assembly;
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> StringCache = new();

    /// <summary>
    /// Gets the names of the templates shipped with the plugin.
    /// </summary>
    public static IReadOnlyList<string> TemplateNames { get; } = new[] { "invitation", "recovery", "confirm-email", "email-changed", "email-change-cancelled", "password-changed" };

    /// <summary>
    /// Renders a template. Values are HTML-encoded in the HTML part and inserted as-is in the text part.
    /// For each part the first match wins: configured override for the language, embedded file for the language,
    /// then the same two for English.
    /// </summary>
    /// <param name="template">The template name, one of <see cref="TemplateNames"/>.</param>
    /// <param name="language">The language code (e.g. <c>fr</c>).</param>
    /// <param name="values">The placeholder values; a missing placeholder renders empty.</param>
    /// <param name="overrides">The overrides keyed <c>{lang}/{template}.{html|txt}</c>; defaults to the plugin configuration.</param>
    /// <returns>The subject, HTML body and text body.</returns>
    public static (string Subject, string Html, string Text) Render(
        string template,
        string language,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        if (!TemplateNames.Contains(template))
        {
            throw new ArgumentException("Unknown e-mail template.", nameof(template));
        }

        overrides ??= Plugin.Instance?.Configuration.TemplateOverrides;
        var lang = NormalizeLanguage(language);
        var htmlSource = Resolve(template, lang, "html", overrides)
            ?? throw new InvalidOperationException("Missing HTML template " + template);
        var txtSource = Resolve(template, lang, "txt", overrides)
            ?? throw new InvalidOperationException("Missing text template " + template);

        var (subjectSource, bodySource) = SplitSubject(txtSource);
        var subject = Substitute(subjectSource, values, encode: false).ReplaceLineEndings(" ").Trim();
        var logoUrl = values.TryGetValue(LogoUrlKey, out var given) ? given : ConfiguredLogoUrl();
        var html = SubstituteAround(htmlSource, LogoRowMarker, LogoRow(logoUrl), values);
        var text = Substitute(bodySource, values, encode: false);
        return (subject, html, text);
    }

    /// <summary>
    /// Gets a user-facing string of the plugin by language, falling back to English.
    /// </summary>
    /// <param name="language">The language code.</param>
    /// <param name="key">The string key.</param>
    /// <returns>The string, or the key itself when unknown.</returns>
    public static string GetString(string language, string key)
    {
        foreach (var lang in new[] { NormalizeLanguage(language), FallbackLanguage })
        {
            var strings = StringCache.GetOrAdd(lang, LoadStrings);
            if (strings.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return key;
    }

    /// <summary>
    /// Gets a string from the strings file of a language only, without any fallback.
    /// </summary>
    /// <param name="language">The language code.</param>
    /// <param name="key">The string key.</param>
    /// <returns>The string, or null when the language file does not define it.</returns>
    internal static string? GetOwnString(string language, string key)
        => StringCache.GetOrAdd(language, LoadStrings).TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// Gets the user-facing strings whose key starts with a prefix, by language, falling back to English per key.
    /// </summary>
    /// <param name="language">The language code.</param>
    /// <param name="prefix">The key prefix.</param>
    /// <returns>The strings by key.</returns>
    public static IReadOnlyDictionary<string, string> GetStrings(string language, string prefix)
    {
        var result = new Dictionary<string, string>();
        foreach (var lang in new[] { FallbackLanguage, NormalizeLanguage(language) })
        {
            foreach (var (key, value) in StringCache.GetOrAdd(lang, LoadStrings))
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    result[key] = value;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Reads an embedded template file.
    /// </summary>
    /// <param name="lang">The language folder.</param>
    /// <param name="template">The template name.</param>
    /// <param name="extension">The extension, <c>html</c> or <c>txt</c>.</param>
    /// <returns>The content, or null when absent.</returns>
    internal static string? ReadEmbedded(string lang, string template, string extension)
        => ReadResource($"Resources.Email.{lang}.{template}.{extension}");

    private static string? Resolve(string template, string lang, string extension, IReadOnlyDictionary<string, string>? overrides)
    {
        foreach (var l in lang == FallbackLanguage ? new[] { lang } : new[] { lang, FallbackLanguage })
        {
            if (overrides is not null
                && overrides.TryGetValue($"{l}/{template}.{extension}", out var custom)
                && !string.IsNullOrWhiteSpace(custom))
            {
                return custom;
            }

            var embedded = ReadEmbedded(l, template, extension);
            if (embedded is not null)
            {
                return embedded;
            }
        }

        return null;
    }

    private static (string Subject, string Body) SplitSubject(string txt)
    {
        var normalized = txt.ReplaceLineEndings("\n");
        var firstBreak = normalized.IndexOf('\n', StringComparison.Ordinal);
        var first = firstBreak < 0 ? normalized : normalized[..firstBreak];
        if (!first.StartsWith(SubjectPrefix, StringComparison.Ordinal))
        {
            return (string.Empty, normalized);
        }

        var body = firstBreak < 0 ? string.Empty : normalized[(firstBreak + 1)..].TrimStart('\n');
        return (first[SubjectPrefix.Length..], body);
    }

    /// <summary>
    /// Builds the absolute URL of the logo served by the public pages.
    /// </summary>
    /// <param name="publicUrl">The configured public base URL.</param>
    /// <returns>The URL, or null when the public URL is not an absolute http(s) URL.</returns>
    internal static string? LogoUrl(string? publicUrl)
    {
        if (string.IsNullOrWhiteSpace(publicUrl)
            || publicUrl.Trim().AsSpan().IndexOfAnyExceptInRange('!', '~') >= 0
            || publicUrl.AsSpan().IndexOfAny(UnsafeUrlChars) >= 0
            || !Uri.TryCreate(publicUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return publicUrl.Trim().TrimEnd('/') + "/MailConcierge/assets/logo.png";
    }

    // A cosmetic logo must never prevent an e-mail from being sent, so an unreadable configuration just means no logo.
    private static string? ConfiguredLogoUrl()
    {
        try
        {
            return LogoUrl(Plugin.Instance?.Configuration.PublicUrl);
        }
#pragma warning disable CA1031 // Any failure reading the configuration only drops the logo.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    // The logo header row of the HTML templates; nothing when there is no logo URL, so no broken image is shown.
    private static string LogoRow(string? logoUrl)
        => string.IsNullOrEmpty(logoUrl)
            ? string.Empty
            : "<tr><td align=\"center\" style=\"padding:28px 32px 0\"><img src=\"" + WebUtility.HtmlEncode(logoUrl)
                + "\" width=\"56\" height=\"56\" alt=\"\" style=\"display:block;border:0;outline:none\"></td></tr>";

    // The logo row goes in after the placeholders are filled, so nothing inside it (the URL) is read as a placeholder.
    private static string SubstituteAround(string source, string marker, string row, IReadOnlyDictionary<string, string> values)
    {
        var at = source.IndexOf(marker, StringComparison.Ordinal);
        return at < 0
            ? Substitute(source, values, encode: true)
            : Substitute(source[..at], values, encode: true) + row + Substitute(source[(at + marker.Length)..], values, encode: true);
    }

    private static string Substitute(string source, IReadOnlyDictionary<string, string> values, bool encode)
        => Placeholder.Replace(source, m =>
        {
            if (!values.TryGetValue(m.Groups[1].Value, out var value))
            {
                return string.Empty;
            }

            return encode ? WebUtility.HtmlEncode(value) : value;
        });

    private static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return FallbackLanguage;
        }

        var code = language.Trim().ToLowerInvariant();
        var cut = code.AsSpan().IndexOfAny(LanguageSeparators);
        return cut > 0 ? code[..cut] : code;
    }

    private static IReadOnlyDictionary<string, string> LoadStrings(string lang)
    {
        var json = ReadResource($"Resources.Strings.{lang}.json");
        return json is null
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
    }

    private static string? ReadResource(string suffix)
    {
        using var stream = Asm.GetManifestResourceStream($"{typeof(EmailTemplates).Namespace!.Replace(".Email", string.Empty, StringComparison.Ordinal)}.{suffix}");
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
