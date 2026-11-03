namespace Jellyfin.Plugin.MailConcierge.Configuration;

/// <summary>
/// One serialized template override (XmlSerializer cannot handle dictionaries).
/// </summary>
public class TemplateOverrideEntry
{
    /// <summary>Gets or sets the override key, <c>{lang}/{template}.{html|txt}</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the template content.</summary>
    public string Value { get; set; } = string.Empty;
}
