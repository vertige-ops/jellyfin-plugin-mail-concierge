namespace Jellyfin.Plugin.MailConcierge.Seerr;

/// <summary>
/// The outcome of an import.
/// </summary>
/// <param name="Imported">The number of addresses stored.</param>
/// <param name="Changed">The number of addresses skipped because Seerr no longer returns the address that was displayed.</param>
public sealed record SeerrImportResult(int Imported, int Changed);
