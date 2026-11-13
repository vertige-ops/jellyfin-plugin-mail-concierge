using System;

namespace Jellyfin.Plugin.MailConcierge.Seerr;

/// <summary>
/// One address the administrator saw in the import preview, posted back to be imported.
/// </summary>
/// <param name="UserId">The Jellyfin user id.</param>
/// <param name="Email">The address displayed for that user.</param>
public sealed record SeerrImportItem(Guid UserId, string Email);
