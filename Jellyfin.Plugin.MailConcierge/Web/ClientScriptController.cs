using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.MailConcierge.Email;
using Jellyfin.Plugin.MailConcierge.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MailConcierge.Web;

/// <summary>
/// Serves the script injected in the web client.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("MailConcierge")]
public sealed class ClientScriptController : ControllerBase
{
    private const string Prefix = "Jellyfin.Plugin.MailConcierge.Resources.Client.";
    private const string StringsPrefix = Prefix + "strings.";
    private const string Placeholder = "/*MAILCONCIERGE_STRINGS*/null";

    // Strings shared with the server side, so that the dialog rewrite and the PIN error use the same sentences.
    private static readonly Dictionary<string, string> ServerKeys = new() { ["forgot.neutral"] = "forgot.neutral", ["pin.error"] = "page.error.invalid" };
    private static readonly Lazy<string> Script = new(Build);

    /// <summary>
    /// Gets the client script, with the user-facing strings of every language inlined.
    /// </summary>
    /// <returns>The script.</returns>
    [HttpGet("client.js")]
    public ActionResult Get()
    {
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
        return Content(Script.Value, "text/javascript; charset=utf-8", Encoding.UTF8);
    }

    private static string Build()
    {
        var asm = typeof(ClientScriptController).Assembly;
        var strings = new SortedDictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        // The languages come from Resources/Strings/{lang}.json; a strings.{lang}.json client file is optional
        // (the script falls back to English for each missing key).
        foreach (var language in Languages.Supported)
        {
            var resource = StringsPrefix + language.Code + ".json";
            var values = asm.GetManifestResourceNames().Contains(resource)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(Read(asm, resource)) ?? new Dictionary<string, string>()
                : new Dictionary<string, string>();
            foreach (var (key, source) in ServerKeys)
            {
                values[key] = EmailTemplates.GetString(language.Code, source);
            }

            values["language.name"] = language.Name;
            strings[language.Code] = values;
        }

        return Read(asm, Prefix + "mailconcierge-client.js").Replace(Placeholder, JsonSerializer.Serialize(strings), StringComparison.Ordinal);
    }

    private static string Read(Assembly asm, string name)
    {
        using var stream = asm.GetManifestResourceStream(name) ?? throw new InvalidOperationException("Missing resource " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
