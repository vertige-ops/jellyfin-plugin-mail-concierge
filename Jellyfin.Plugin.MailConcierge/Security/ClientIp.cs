using System;
using System.Net;

namespace Jellyfin.Plugin.MailConcierge.Security;

/// <summary>
/// Computes rate limit keys from client addresses.
/// </summary>
public static class ClientIp
{
    /// <summary>
    /// Returns the key for an address: IPv4 unchanged, IPv4-mapped IPv6 unwrapped, other IPv6 as its /64 prefix.
    /// </summary>
    /// <param name="address">The client address (HttpContext.Connection.RemoteIpAddress).</param>
    /// <returns>The key.</returns>
    public static string Key(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString() + "/64";
    }
}
