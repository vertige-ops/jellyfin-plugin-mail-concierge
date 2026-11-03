using System;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MailConcierge.Seerr;

/// <summary>
/// An <see cref="ISeerrSync"/> that does nothing, used until the Seerr integration replaces it.
/// </summary>
public sealed class NullSeerrSync : ISeerrSync
{
    /// <inheritdoc />
    public Task PushAsync(Guid userId) => Task.CompletedTask;
}
