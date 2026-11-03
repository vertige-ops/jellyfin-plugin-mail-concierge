using System;
using System.Linq;
using System.Net;
using System.Threading;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.MailConcierge.Security;

/// <summary>
/// Detects a reverse proxy unknown to Jellyfin, which would make every client share one address.
/// </summary>
/// <remarks>
/// Jellyfin 12.1 sets <c>ForwardedHeaders.None</c> when <c>KnownProxies</c> is empty
/// (ApiServiceCollectionExtensions), so <c>Connection.RemoteIpAddress</c> is then the raw socket peer.
/// A private peer sending <c>X-Forwarded-For</c> while no proxy is known is therefore a proxy Jellyfin ignores.
/// </remarks>
public sealed class ProxyGuard
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly IServerConfigurationManager _config;
    private readonly TimeProvider _time;
    private volatile string? _reason;
    private long _observedAt;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProxyGuard"/> class.
    /// </summary>
    /// <param name="config">The server configuration manager.</param>
    /// <param name="time">The time source; defaults to the system clock.</param>
    public ProxyGuard(IServerConfigurationManager config, TimeProvider? time = null)
    {
        _config = config;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Gets a value indicating whether an unknown reverse proxy was detected.</summary>
    public bool Tripped => Reason is not null;

    /// <summary>
    /// Gets the explanation shown to the administrator, or null when not tripped. The state ends by itself when
    /// Known Proxies is set, or after 15 minutes without a request that shows an unknown proxy.
    /// </summary>
    public string? Reason
    {
        get
        {
            if (_reason is not null && (HasKnownProxies() || _time.GetElapsedTime(Volatile.Read(ref _observedAt)) > Lifetime))
            {
                _reason = null;
            }

            return _reason;
        }
    }

    /// <summary>Gets the stable code of <see cref="Reason"/> for the admin page to localize, or null when not tripped.</summary>
    public string? ReasonCode => Reason is null ? null : "unknown_proxy";

    /// <summary>
    /// Inspects a request and trips the guard when it comes through an unknown reverse proxy.
    /// </summary>
    /// <param name="ctx">The HTTP context.</param>
    public void Observe(HttpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (HasKnownProxies())
        {
            _reason = null;
            return;
        }

        var peer = ctx.Connection.RemoteIpAddress;
        if (peer is null || !IsPrivate(peer) || !ctx.Request.Headers.ContainsKey("X-Forwarded-For"))
        {
            return;
        }

        Volatile.Write(ref _observedAt, _time.GetTimestamp());
        _reason = "Mail Concierge detected a reverse proxy that Jellyfin does not know about: requests come from a private address "
            + "but carry an X-Forwarded-For header. Set Known Proxies in Dashboard > Networking, then Mail Concierge can be used. "
            + "This notice clears by itself after 15 minutes without such a request.";
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var b = address.GetAddressBytes();
        if (b.Length == 4)
        {
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);
    }

    private bool HasKnownProxies()
        => _config.GetNetworkConfiguration().KnownProxies.Any(p => !string.IsNullOrWhiteSpace(p));
}
