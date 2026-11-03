namespace Jellyfin.Plugin.MailConcierge.Localization;

/// <summary>
/// A language shipped with the plugin.
/// </summary>
/// <param name="Code">The language code, for example <c>fr</c>.</param>
/// <param name="Name">The name of the language in itself, for example <c>Français</c>.</param>
public sealed record LanguageInfo(string Code, string Name);
