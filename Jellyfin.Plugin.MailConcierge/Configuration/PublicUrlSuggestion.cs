using System;

namespace Jellyfin.Plugin.MailConcierge.Configuration;

/// <summary>
/// Reads the public address an administrator already declared in Jellyfin (the published server addresses).
/// The result is only a suggestion for the settings page: it is never stored.
/// </summary>
public static class PublicUrlSuggestion
{
    /// <summary>
    /// Picks the address to suggest from the published server addresses of Jellyfin (entries <c>key=address</c>).
    /// </summary>
    /// <param name="publishedServerUris">The entries; the key is a subnet, <c>all</c> or <c>external</c>.</param>
    /// <returns>The address of the <c>all</c> entry, else of the <c>external</c> entry, when it is an absolute http or https URL; otherwise null.</returns>
    public static string? From(string[]? publishedServerUris)
    {
        if (publishedServerUris is null)
        {
            return null;
        }

        // An "all" entry that is present but unusable is not replaced by "external".
        var address = AddressOf(publishedServerUris, "all") ?? AddressOf(publishedServerUris, "external");
        return Uri.TryCreate(address, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && !string.IsNullOrEmpty(uri.Host)
            ? address
            : null;
    }

    // The text after "=" of the first entry with this key (case-insensitive), or null when there is none.
    private static string? AddressOf(string[] entries, string key)
    {
        foreach (var entry in entries)
        {
            var separator = entry?.IndexOf('=', StringComparison.Ordinal) ?? -1;
            if (separator >= 0 && string.Equals(entry![..separator].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                return entry[(separator + 1)..].Trim();
            }
        }

        return null;
    }
}
