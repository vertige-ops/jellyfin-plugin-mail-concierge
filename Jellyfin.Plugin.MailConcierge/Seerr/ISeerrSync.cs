using System;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MailConcierge.Seerr;

/// <summary>
/// Pushes the Mail Concierge view of a user (e-mail address) to Seerr.
/// </summary>
public interface ISeerrSync
{
    /// <summary>
    /// Pushes the current e-mail address of a user to Seerr. Implementations must not throw for a missing or unreachable Seerr.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <returns>A task that completes when the push was attempted.</returns>
    Task PushAsync(Guid userId);
}
