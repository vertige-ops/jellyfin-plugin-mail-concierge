using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.MailConcierge.Seerr;

/// <summary>
/// Scheduled task pushing to Seerr the addresses that could not be pushed earlier. Jellyfin discovers it by type scanning
/// and builds it with the services of the plugin.
/// </summary>
public sealed class SeerrSyncTask : IScheduledTask
{
    private readonly SeerrSync _sync;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeerrSyncTask"/> class.
    /// </summary>
    /// <param name="sync">The Seerr synchronisation.</param>
    public SeerrSyncTask(SeerrSync sync) => _sync = sync;

    /// <inheritdoc />
    public string Name => "Mail Concierge: Seerr e-mail sync";

    /// <inheritdoc />
    public string Key => "MailConciergeSeerrEmailSync";

    /// <inheritdoc />
    public string Description => "Pushes to Seerr the confirmed e-mail addresses that could not be pushed yet.";

    /// <inheritdoc />
    public string Category => "Mail Concierge";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();
        await _sync.RetryPendingAsync().ConfigureAwait(false);
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(6).Ticks };
    }
}
