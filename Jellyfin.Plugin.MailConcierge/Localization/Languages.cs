using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.MailConcierge.Email;
using MediaBrowser.Controller;

namespace Jellyfin.Plugin.MailConcierge.Localization;

/// <summary>
/// The single source of truth for the languages: the ones discovered from the embedded
/// <c>Resources/Strings/{code}.json</c> files. Adding a language means adding files, not changing code.
/// </summary>
public static class Languages
{
    /// <summary>
    /// The language used when nothing else matches. It must always ship.
    /// </summary>
    public const string Fallback = "en";

    private const string StringsPrefix = "Jellyfin.Plugin.MailConcierge.Resources.Strings.";
    private const string NameKey = "language.name";

    private static readonly SearchValues<char> Separators = SearchValues.Create("-_");
    private static readonly Lazy<IReadOnlyList<LanguageInfo>> Discovered = new(Discover);

    /// <summary>
    /// Gets the supported languages, ordered by code.
    /// </summary>
    public static IReadOnlyList<LanguageInfo> Supported => Discovered.Value;

    /// <summary>
    /// Tells whether a bare language code is supported.
    /// </summary>
    /// <param name="code">The code, for example <c>fr</c>.</param>
    /// <returns>True when a strings file exists for it.</returns>
    public static bool IsSupported(string? code) => code is not null && Supported.Any(l => string.Equals(l.Code, code, StringComparison.Ordinal));

    /// <summary>
    /// Reduces a language tag such as <c>fr-FR</c> to a supported code.
    /// </summary>
    /// <param name="language">The tag or code.</param>
    /// <returns>The supported code, or null.</returns>
    public static string? Parse(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var code = language.Trim().ToLowerInvariant();
        var cut = code.AsSpan().IndexOfAny(Separators);
        if (cut > 0)
        {
            code = code[..cut];
        }

        return IsSupported(code) ? code : null;
    }

    /// <summary>
    /// Gets the language of every e-mail sent to an existing account, and of the pages reached from them: the configured default language.
    /// </summary>
    /// <param name="defaultLanguage">The configured default language.</param>
    /// <returns>A supported language code; <see cref="Fallback"/> when the setting is not usable.</returns>
    public static string ForAccounts(string? defaultLanguage) => Parse(defaultLanguage) ?? Fallback;

    /// <summary>
    /// Picks a supported language: the requested one, else the default one, else English.
    /// </summary>
    /// <param name="language">The requested language tag or code (may be null).</param>
    /// <param name="defaultLanguage">The configured default language.</param>
    /// <returns>A supported code.</returns>
    public static string Normalize(string? language, string? defaultLanguage) => Parse(language) ?? Parse(defaultLanguage) ?? Fallback;

    /// <summary>
    /// Gets the name of the server for texts, falling back to <c>Jellyfin</c> when the administrator gave none
    /// (Jellyfin then reports the machine name, which in a container is an unreadable identifier).
    /// </summary>
    /// <param name="host">The application host.</param>
    /// <returns>The server name.</returns>
    public static string ServerName(IServerApplicationHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var name = host.FriendlyName;
        return string.IsNullOrWhiteSpace(name) || string.Equals(name, Environment.MachineName, StringComparison.OrdinalIgnoreCase) ? "Jellyfin" : name;
    }

    /// <summary>
    /// Formats a UTC date as a long local date in a language, for example <c>7 November 2026</c>.
    /// </summary>
    /// <param name="utc">The UTC date.</param>
    /// <param name="zone">The time zone to show it in.</param>
    /// <param name="language">The language code.</param>
    /// <returns>The text.</returns>
    public static string FormatDate(DateTime utc, TimeZoneInfo zone, string language)
        => TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString(DatePattern(language), CultureOf(language));

    /// <summary>
    /// Formats an instant as a date and a time (to the minute) in a time zone, for short validity periods.
    /// </summary>
    /// <param name="utc">The instant, in UTC.</param>
    /// <param name="zone">The time zone to show it in.</param>
    /// <param name="language">The language code.</param>
    /// <returns>The text.</returns>
    public static string FormatDateTime(DateTime utc, TimeZoneInfo zone, string language)
        => TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString(DatePattern(language) + (language == "de" ? ", HH:mm 'Uhr'" : " HH:mm"), CultureOf(language));

    private static string DatePattern(string language) => language switch
    {
        "es" => "d 'de' MMMM 'de' yyyy",
        "de" => "d. MMMM yyyy",
        _ => "d MMMM yyyy",
    };

    private static CultureInfo CultureOf(string language)
    {
        try
        {
            return CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    private static IReadOnlyList<LanguageInfo> Discover()
    {
        var result = new List<LanguageInfo>();
        foreach (var resource in typeof(Languages).Assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(StringsPrefix, StringComparison.Ordinal) || !resource.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }

            var code = resource[StringsPrefix.Length..^".json".Length];
            if (code.Length == 0 || code.Contains('.', StringComparison.Ordinal))
            {
                continue;
            }

            var name = EmailTemplates.GetOwnString(code, NameKey);
            result.Add(new LanguageInfo(code, string.IsNullOrWhiteSpace(name) ? code : name));
        }

        return result.OrderBy(l => l.Code, StringComparer.Ordinal).ToList();
    }
}
