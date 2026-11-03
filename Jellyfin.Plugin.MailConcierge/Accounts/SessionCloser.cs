using System;
using System.Threading.Tasks;
using Jellyfin.Data.Queries;
using Jellyfin.Plugin.MailConcierge.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MailConcierge.Accounts;

/// <summary>
/// Signs a user out of their devices after a password reset, according to the configured policy.
/// </summary>
public sealed class SessionCloser
{
    private readonly IDeviceManager _devices;
    private readonly ISessionManager _sessions;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionCloser> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionCloser"/> class.
    /// </summary>
    /// <param name="devices">The device manager.</param>
    /// <param name="sessions">The session manager.</param>
    /// <param name="time">The time source.</param>
    /// <param name="logger">The logger.</param>
    public SessionCloser(IDeviceManager devices, ISessionManager sessions, TimeProvider time, ILogger<SessionCloser> logger)
    {
        _devices = devices;
        _sessions = sessions;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Logs the user out of every device, or only of those inactive for too long, or of none.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="cfg">The configuration holding the policy.</param>
    /// <returns>A task that completes when the devices were processed.</returns>
    public async Task CloseAsync(Guid userId, PluginConfiguration cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (cfg.SessionsOnReset == SessionClosePolicy.None)
        {
            return;
        }

        var cutoff = _time.GetUtcNow().UtcDateTime.AddDays(-cfg.SessionInactiveDays);
        var closed = 0;
        foreach (var device in _devices.GetDevices(new DeviceQuery { UserId = userId }).Items)
        {
            if (cfg.SessionsOnReset == SessionClosePolicy.InactiveSinceDays && device.DateLastActivity >= cutoff)
            {
                continue;
            }

            try
            {
                await _sessions.Logout(device).ConfigureAwait(false);
                closed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not log user {UserId} out of a device after a password reset", userId);
            }
        }

        _logger.LogInformation("Closed {Count} session(s) of user {UserId} after a password reset", closed, userId);
    }
}
