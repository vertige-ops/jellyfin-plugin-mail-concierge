using System;
using Jellyfin.Plugin.MailConcierge.Configuration;

namespace Jellyfin.Plugin.MailConcierge.Security;

/// <summary>
/// The one answer to "is the service of Mail Concierge open?": it is closed while the plugin is disabled or the proxy guard is tripped.
/// Every public and sending path asks it first, before any token, failure count or e-mail budget is touched.
/// </summary>
public sealed class ServiceGate
{
    private readonly Func<PluginConfiguration> _config;
    private readonly ProxyGuard _guard;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceGate"/> class.
    /// </summary>
    /// <param name="config">Accessor of the current configuration.</param>
    /// <param name="guard">The reverse-proxy guard.</param>
    public ServiceGate(Func<PluginConfiguration> config, ProxyGuard guard)
    {
        _config = config;
        _guard = guard;
    }

    /// <summary>Gets a value indicating whether the plugin is enabled and no unknown reverse proxy was detected.</summary>
    public bool IsOpen => _config().Enabled && !_guard.Tripped;
}
