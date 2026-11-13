using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mime;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MailConcierge.Accounts;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Email;
using Jellyfin.Plugin.MailConcierge.Localization;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.MailConcierge.Web;

/// <summary>
/// Renders the public pages from the embedded templates.
/// </summary>
public sealed class PageRenderer
{
    private const string Prefix = "Jellyfin.Plugin.MailConcierge.Resources.Pages.";
    private const string StringPrefix = "page.";

    private static readonly Regex Placeholder = new(@"\{\{(s:)?([\w.]+)\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Assembly Asm = typeof(PageRenderer).Assembly;

    private readonly IServerApplicationHost _host;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly Func<PluginConfiguration> _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="PageRenderer"/> class.
    /// </summary>
    /// <param name="host">The application host (server name).</param>
    /// <param name="serverConfig">The server configuration manager (base URL).</param>
    /// <param name="config">Accessor of the current configuration.</param>
    public PageRenderer(IServerApplicationHost host, IServerConfigurationManager serverConfig, Func<PluginConfiguration> config)
    {
        _host = host;
        _serverConfig = serverConfig;
        _config = config;
    }

    /// <summary>
    /// Gets the base path of the server, without trailing slash (empty when Jellyfin is served from the root).
    /// </summary>
    public string BasePath => _serverConfig.GetNetworkConfiguration().BaseUrl;

    /// <summary>
    /// Reads an embedded page resource.
    /// </summary>
    /// <param name="name">The file name, for example <c>pages.css</c>.</param>
    /// <returns>The content, or null when absent.</returns>
    public static string? ReadResource(string name)
    {
        using var stream = Asm.GetManifestResourceStream(Prefix + name);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Reads a binary embedded resource of the pages.
    /// </summary>
    /// <param name="name">The file name.</param>
    /// <returns>The bytes, or null when the resource does not exist.</returns>
    public static byte[]? ReadResourceBytes(string name)
    {
        using var stream = Asm.GetManifestResourceStream(Prefix + name);
        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Picks the page language from an Accept-Language header, limited to the supported languages; English when none matches
    /// (the e-mail language setting is for e-mails, not for what the browser asks).
    /// </summary>
    /// <param name="acceptLanguage">The header value.</param>
    /// <returns>A supported language code.</returns>
    public string PickLanguage(string? acceptLanguage)
    {
        var best = string.IsNullOrWhiteSpace(acceptLanguage)
            ? Array.Empty<string>()
            : acceptLanguage.Split(',')
                .Select((part, index) => (Part: part.Trim(), Index: index))
                .Select(x => Parse(x.Part, x.Index))
                .Where(x => x.Q > 0 && Languages.IsSupported(x.Code))
                .OrderByDescending(x => x.Q)
                .ThenBy(x => x.Index)
                .Select(x => x.Code)
                .ToArray();
        if (best.Length > 0)
        {
            return best[0];
        }

        return Languages.Fallback;
    }

    /// <summary>
    /// Renders a page.
    /// </summary>
    /// <param name="page">The page name: reset, confirm, cancel, accept, message or toomany.</param>
    /// <param name="language">The language code.</param>
    /// <param name="model">An object whose public properties are available to the template as placeholders.</param>
    /// <returns>The result carrying the nonce to put in the security headers.</returns>
    public PageResult Render(string page, string language, object model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var lang = Languages.IsSupported(language) ? language : Languages.Fallback;
        var strings = EmailTemplates.GetStrings(lang, StringPrefix);
        var serverName = Languages.ServerName(_host);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        var basePath = BasePath;

        var values = new Dictionary<string, string>
        {
            ["base"] = basePath,
            ["serverName"] = serverName,
        };
        foreach (var property in model.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0))
        {
            values[property.Name] = Convert.ToString(property.GetValue(model), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        var content = Fill(ReadResource(page + ".html") ?? throw new ArgumentException("Unknown page.", nameof(page)), values, strings, null);
        var clientStrings = new Dictionary<string, string>(strings) { ["password.tooShort"] = EmailTemplates.GetString(lang, "password.tooShort") };
        var json = JsonSerializer.Serialize(new { page, lang, @base = basePath, minPasswordLength = RecoveryService.MinPasswordLength, strings = clientStrings });
        var layout = Fill(
            ReadResource("layout.html")!,
            new Dictionary<string, string>
            {
                ["lang"] = lang,
                ["title"] = strings.GetValueOrDefault("page." + TitleKey(page) + ".title") ?? serverName,
                ["serverName"] = serverName,
                ["base"] = basePath,
                ["nonce"] = nonce,
            },
            strings,
            new Dictionary<string, string> { ["content"] = content, ["data"] = json });
        return new PageResult(nonce)
        {
            Content = layout,
            ContentType = MediaTypeNames.Text.Html + "; charset=utf-8",
            StatusCode = StatusCodes.Status200OK,
        };
    }

    private static string TitleKey(string page) => page switch
    {
        "message" => "invalid",
        "toomany" => "tooMany",
        _ => page,
    };

    private static (string Code, double Q, int Index) Parse(string part, int index)
    {
        var pieces = part.Split(';');
        var tag = pieces[0].Trim().ToLowerInvariant();
        var dash = tag.IndexOf('-', StringComparison.Ordinal);
        var code = dash > 0 ? tag[..dash] : tag;
        var q = 1d;
        foreach (var parameter in pieces.Skip(1))
        {
            var kv = parameter.Trim();
            if (kv.StartsWith("q=", StringComparison.OrdinalIgnoreCase))
            {
                q = double.TryParse(kv[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0d;
            }
        }

        return (code, q, index);
    }

    private static string Fill(string template, IReadOnlyDictionary<string, string> values, IReadOnlyDictionary<string, string> strings, IReadOnlyDictionary<string, string>? raw)
        => Placeholder.Replace(template, m =>
        {
            var key = m.Groups[2].Value;
            if (m.Groups[1].Success)
            {
                return WebUtility.HtmlEncode(strings.GetValueOrDefault(key) ?? key);
            }

            if (raw is not null && raw.TryGetValue(key, out var rawValue))
            {
                return rawValue;
            }

            return WebUtility.HtmlEncode(values.GetValueOrDefault(key) ?? string.Empty);
        });
}
