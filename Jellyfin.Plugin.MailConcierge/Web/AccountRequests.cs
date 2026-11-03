namespace Jellyfin.Plugin.MailConcierge.Web;

#pragma warning disable SA1402, SA1649 // Small related request types kept together.

/// <summary>
/// The body of <c>POST /MailConcierge/Me/Email</c>.
/// </summary>
/// <param name="Email">The new address.</param>
/// <param name="CurrentPassword">The current password of the user.</param>
public sealed record MeEmailRequest(string? Email, string? CurrentPassword);

/// <summary>
/// The body of <c>PUT /MailConcierge/Users/{id}/Email</c>.
/// </summary>
/// <param name="Email">The address, or empty to remove it.</param>
public sealed record AdminEmailRequest(string? Email);

/// <summary>
/// The body of <c>POST /MailConcierge/Seerr/Import</c>.
/// </summary>
/// <param name="UserIds">The Jellyfin ids of the users whose Seerr address is imported.</param>
public sealed record SeerrImportRequest(System.Collections.Generic.IReadOnlyList<System.Guid>? UserIds);
