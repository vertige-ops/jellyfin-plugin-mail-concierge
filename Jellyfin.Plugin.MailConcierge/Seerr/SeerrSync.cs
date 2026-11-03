using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.MailConcierge.Configuration;
using Jellyfin.Plugin.MailConcierge.Data;
using Jellyfin.Plugin.MailConcierge.Localization;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MailConcierge.Seerr;

/// <summary>
/// One-way copy of the confirmed e-mail addresses to Seerr, and import of the addresses Seerr already holds.
/// </summary>
/// <remarks>
/// Decision: users that cannot be pushed now (not yet known to Seerr, or Seerr unreachable) are kept in the
/// <c>PendingSeerr</c> set and retried by the scheduled task. A refusal by Seerr (address used by another Seerr user) is final.
/// </remarks>
public sealed class SeerrSync : ISeerrSync
{
    private readonly MailConciergeStore _store;
    private readonly SeerrClient _client;
    private readonly IUserManager _users;
    private readonly Func<PluginConfiguration> _config;
    private readonly TimeProvider _time;
    private readonly ILogger<SeerrSync> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeerrSync"/> class.
    /// </summary>
    /// <param name="store">The data store.</param>
    /// <param name="client">The Seerr client.</param>
    /// <param name="users">The user manager.</param>
    /// <param name="config">Accessor of the current configuration.</param>
    /// <param name="time">The time source.</param>
    /// <param name="logger">The logger.</param>
    public SeerrSync(MailConciergeStore store, SeerrClient client, IUserManager users, Func<PluginConfiguration> config, TimeProvider time, ILogger<SeerrSync> logger)
    {
        _store = store;
        _client = client;
        _users = users;
        _config = config;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PushAsync(Guid userId)
    {
        try
        {
            if (!_config().SeerrEnabled)
            {
                return;
            }

            if (!await TryPushAsync(userId).ConfigureAwait(false))
            {
                await _store.WriteAsync(d =>
                {
                    if (!d.PendingSeerr.Contains(userId))
                    {
                        d.PendingSeerr.Add(userId);
                    }
                }).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Seerr push failed for user {UserId}", userId);
        }
    }

    /// <summary>
    /// Pushes the users that could not be pushed earlier.
    /// </summary>
    /// <returns>The number of users pushed.</returns>
    public async Task<int> RetryPendingAsync()
    {
        try
        {
            if (!_config().SeerrEnabled)
            {
                return 0;
            }

            var pending = await _store.ReadAsync(d => d.PendingSeerr.ToList()).ConfigureAwait(false);
            var done = 0;
            foreach (var userId in pending)
            {
                if (await TryPushAsync(userId).ConfigureAwait(false))
                {
                    done++;
                    await _store.WriteAsync(d => d.PendingSeerr.Remove(userId)).ConfigureAwait(false);
                }
            }

            return done;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Seerr retry failed");
            return 0;
        }
    }

    /// <summary>
    /// Pushes the pending users now, for an administrator: unlike the background retry, it says when Seerr cannot be used.
    /// </summary>
    /// <returns>The number of users pushed.</returns>
    /// <exception cref="SeerrUnavailableException">Seerr is disabled or unreachable.</exception>
    public async Task<int> SyncNowAsync()
    {
        if (!_config().SeerrEnabled)
        {
            throw new SeerrUnavailableException(SeerrUnavailableException.Disabled);
        }

        try
        {
            // Cheap probe: 404 (unknown user) is a valid answer, a network error or a refused key is not.
            await _client.FindUserIdAsync(Guid.Empty).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Seerr is not reachable ({Reason})", ex.GetType().Name);
            throw new SeerrUnavailableException(SeerrUnavailableException.Unreachable, ex);
        }

        return await RetryPendingAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Lists the Seerr addresses that could be imported: Seerr users linked to an existing Jellyfin user, with a real address
    /// (not Seerr's placeholder, which is the user name), different from the one stored.
    /// </summary>
    /// <returns>The candidates.</returns>
    /// <exception cref="SeerrUnavailableException">Seerr is disabled or unreachable.</exception>
    public async Task<IReadOnlyList<ImportCandidate>> PreviewImportAsync()
    {
        if (!_config().SeerrEnabled)
        {
            throw new SeerrUnavailableException(SeerrUnavailableException.Disabled);
        }

        IReadOnlyList<(int Id, Guid? JellyfinUserId, string Email)> seerrUsers;
        try
        {
            seerrUsers = await _client.ListUsersAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Seerr import preview failed ({Reason})", ex.GetType().Name);
            throw new SeerrUnavailableException(SeerrUnavailableException.Unreachable, ex);
        }

        var stored = await _store.ReadAsync(d => d.Emails.ToDictionary(e => e.UserId, e => e.Email)).ConfigureAwait(false);
        var candidates = new List<ImportCandidate>();
        foreach (var (_, jellyfinId, email) in seerrUsers)
        {
            if (jellyfinId is not Guid id || _users.GetUserById(id) is not { } user
                || !email.Contains('@', StringComparison.Ordinal)
                || string.Equals(email, user.Username, StringComparison.OrdinalIgnoreCase)
                || !Accounts.EmailService.TryNormalize(email, out var address))
            {
                continue;
            }

            stored.TryGetValue(id, out var current);
            if (!string.Equals(current, address, StringComparison.OrdinalIgnoreCase) && candidates.All(c => c.UserId != id))
            {
                candidates.Add(new ImportCandidate(id, user.Username, address, current));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Stores the selected candidates as confirmed addresses. The addresses are read from Seerr again, never taken from the caller.
    /// Nothing is pushed back to Seerr.
    /// </summary>
    /// <param name="userIds">The Jellyfin user ids to import.</param>
    /// <returns>The number of addresses stored.</returns>
    public async Task<int> ApplyImportAsync(IEnumerable<Guid> userIds)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        var selected = userIds.ToHashSet();
        var chosen = (await PreviewImportAsync().ConfigureAwait(false)).Where(c => selected.Contains(c.UserId)).ToList();
        if (chosen.Count == 0)
        {
            return 0;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var fallback = Languages.Normalize(null, _config().DefaultLanguage);
        await _store.WriteAsync(d =>
        {
            foreach (var candidate in chosen)
            {
                var index = d.Emails.FindIndex(e => e.UserId == candidate.UserId);
                var updated = new UserEmail(candidate.UserId, candidate.Email, null, index >= 0 ? d.Emails[index].Language : fallback, now);
                if (index >= 0)
                {
                    d.Emails[index] = updated;
                }
                else
                {
                    d.Emails.Add(updated);
                }

                d.Tokens.RemoveAll(t => t.UserId == candidate.UserId && t.Purpose == TokenPurpose.ConfirmEmail);
            }
        }).ConfigureAwait(false);
        return chosen.Count;
    }

    /// <summary>Pushes one user. Returns false when it must be retried later.</summary>
    private async Task<bool> TryPushAsync(Guid userId)
    {
        var record = await _store.ReadAsync(d => d.Emails.FirstOrDefault(e => e.UserId == userId)).ConfigureAwait(false);
        if (record is not { ConfirmedAt: not null, Email: { Length: > 0 } email })
        {
            // Nothing to push (removed or unconfirmed since): not a failure.
            return true;
        }

        try
        {
            if (await _client.FindUserIdAsync(userId).ConfigureAwait(false) is not int seerrId)
            {
                _logger.LogInformation("User {UserId} is not known to Seerr yet; the push will be retried", userId);
                return false;
            }

            // A refusal by Seerr is final: it is logged by the client and not retried.
            await _client.SetEmailAsync(seerrId, email).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Seerr push for user {UserId} failed ({Reason}); it will be retried", userId, ex.GetType().Name);
            return false;
        }
    }
}
