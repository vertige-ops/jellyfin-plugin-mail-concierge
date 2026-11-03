using System;

namespace Jellyfin.Plugin.MailConcierge.Seerr;

/// <summary>A Seerr address an administrator can import.</summary>
/// <param name="UserId">The Jellyfin user id.</param>
/// <param name="Username">The Jellyfin user name.</param>
/// <param name="Email">The address held by Seerr.</param>
/// <param name="CurrentEmail">The address Mail Concierge holds, if any.</param>
public sealed record ImportCandidate(Guid UserId, string Username, string Email, string? CurrentEmail);
