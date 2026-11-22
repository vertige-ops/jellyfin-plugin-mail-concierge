using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jellyfin.Plugin.MailConcierge.Configuration;

/// <summary>
/// The SMTP presets shipped with the plugin, read once from the embedded data file.
/// </summary>
public static class SmtpPresets
{
    private const string ResourceName = "Jellyfin.Plugin.MailConcierge.Resources.Data.smtp-presets.json";

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static readonly Lazy<IReadOnlyList<SmtpPreset>> Loaded = new(Load);

    /// <summary>
    /// Gets the presets, in the order of the data file.
    /// </summary>
    public static IReadOnlyList<SmtpPreset> All => Loaded.Value;

    private static IReadOnlyList<SmtpPreset> Load()
    {
        using var stream = typeof(SmtpPresets).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Missing resource " + ResourceName);
        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<List<SmtpPreset>>(reader.ReadToEnd(), Options)?.AsReadOnly()
            ?? throw new InvalidOperationException("Empty resource " + ResourceName);
    }
}
