using System.Net;

namespace MuSite.Auth;

/// <summary>Turns a caller into a rate-limit partition key.</summary>
public static class ClientPartition
{
    /// <summary>
    /// A key for the caller's address.
    ///
    /// IPv6 addresses are masked to their /64 prefix. A residential IPv6 allocation is a /64 or
    /// larger, so per-address limiting is no limit at all: an attacker walks through 18 quintillion
    /// addresses in one prefix and every request looks like a new visitor. IPv4 is used whole.
    ///
    /// A request with no remote address at all - which should not happen behind a correctly
    /// configured proxy - lands in one shared bucket rather than escaping the limiter.
    /// </summary>
    public static string For(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }
}
