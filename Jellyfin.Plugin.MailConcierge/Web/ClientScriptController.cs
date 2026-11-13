using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.MailConcierge.Accounts;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Email;
using Jellyfin.Plugin.MailConcierge.Localization;
using Jellyfin.Plugin.MailConcierge.Security;
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
    private const string FlagsPlaceholder = "/*MAILCONCIERGE_FLAGS*/null";

    // Strings shared with the server side, so that the dialog rewrite and the PIN error use the same sentences.
    private static readonly Dictionary<string, string> ServerKeys = new() { ["forgot.neutral"] = "forgot.neutral", ["pin.error"] = "page.error.invalid" };
    private static readonly Lazy<string> Script = new(Build);

    private readonly Func<PluginConfiguration> _config;
    private readonly ServiceGate _gate;
    private readonly EmailLoginProbe _probe;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientScriptController"/> class.
    /// </summary>
    /// <param name="config">Accessor of the current configuration.</param>
    /// <param name="gate">Tells whether the service is open (enabled, proxy guard quiet).</param>
    /// <param name="probe">Tells whether e-mail sign-in can work with this Jellyfin version.</param>
    public ClientScriptController(Func<PluginConfiguration> config, ServiceGate gate, EmailLoginProbe probe)
    {
        _config = config;
        _gate = gate;
        _probe = probe;
    }

    /// <summary>
    /// Gets the client script, with the user-facing strings of every language inlined.
    /// </summary>
    /// <returns>The script, with the flags (only <c>emailLogin</c>: the script is served to signed-out visitors).</returns>
    [HttpGet("client.js")]
    public ActionResult Get()
    {
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.XContentTypeOptions = "nosniff";
        var emailLogin = _config().EmailLogin && _gate.IsOpen && _probe.IsAvailable;
        var flags = JsonSerializer.Serialize(new { emailLogin });
        return Content(Script.Value.Replace(FlagsPlaceholder, flags, StringComparison.Ordinal), "text/javascript; charset=utf-8", Encoding.UTF8);
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
