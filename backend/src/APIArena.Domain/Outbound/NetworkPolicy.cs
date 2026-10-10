using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace APIArena.Domain.Outbound;

public sealed record ValidatedTarget(Uri Uri, string Host, int Port, string PathAndQuery, bool IsDevLocalException);

/// <summary>
/// Syntactic URL policy for user-declared outbound targets (WH-02). It never resolves DNS: address checks happen
/// on every connection in <see cref="IpPolicy"/> (so DNS rebinding cannot bypass them).
/// </summary>
public static partial class UrlPolicy
{
    public const int MaxLength = 2048;

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$")]
    private static partial Regex HostName();

    // Hosts made only of digits, hex, dots and 'x' are alternative IPv4 notations (2130706433, 0x7f.1, 0177.0.0.1, 127.1).
    [GeneratedRegex(@"^(0x[0-9a-f]+|[0-9]+)(\.(0x[0-9a-f]+|[0-9]+)){0,3}\.?$")]
    private static partial Regex NumericHost();

    private static readonly string[] BlockedSuffixes =
        [".localhost", ".local", ".internal", ".intranet", ".lan", ".home", ".corp", ".home.arpa", ".arpa", ".onion", ".test", ".invalid", ".example"];

    private static readonly string[] BlockedHosts = ["localhost", "metadata.google.internal", "metadata", "instance-data"];

    /// <param name="devLocalOrigins">Exact http(s)://host:port origins allowed in Development only (sample receiver).</param>
    public static (ValidatedTarget? Target, string? Error) Validate(string? raw, IReadOnlyCollection<int> allowedPorts, IReadOnlyCollection<string>? devLocalOrigins = null)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxLength)
        {
            return (null, "url_invalid");
        }

        if (raw.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || raw.Contains('\\'))
        {
            return (null, "url_control_characters");
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return (null, "url_invalid");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || raw.Contains('@'))
        {
            return (null, "url_userinfo_not_allowed");
        }

        if (!string.IsNullOrEmpty(uri.Fragment) || raw.Contains('#'))
        {
            return (null, "url_fragment_not_allowed");
        }

        var origin = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        if (devLocalOrigins is { Count: > 0 } && devLocalOrigins.Any(o => string.Equals(o.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase)))
        {
            return (new ValidatedTarget(uri, uri.Host.ToLowerInvariant(), uri.Port, uri.PathAndQuery, IsDevLocalException: true), null);
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return (null, "url_https_required");
        }

        if (!allowedPorts.Contains(uri.Port))
        {
            return (null, "url_port_not_allowed");
        }

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return (null, "url_ip_literal_not_allowed");
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (NumericHost().IsMatch(host) || IPAddress.TryParse(host, out _))
        {
            return (null, "url_ip_literal_not_allowed");
        }

        if (BlockedHosts.Contains(host) || BlockedSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal)))
        {
            return (null, "url_host_not_allowed");
        }

        if (!HostName().IsMatch(host) || host.Length > 253)
        {
            return (null, "url_host_invalid");
        }

        return (new ValidatedTarget(uri, host, uri.Port, uri.PathAndQuery, IsDevLocalException: false), null);
    }
}

/// <summary>
/// Address policy applied to EVERY resolved address of EVERY new connection (WH-03). Only globally routable unicast
/// addresses pass; anything private, loopback, link-local (incl. cloud metadata 169.254.169.254), CGNAT, multicast,
/// reserved, documentation, IPv4-mapped/translated IPv6 or unique-local is rejected.
/// </summary>
public static class IpPolicy
{
    private static readonly (IPAddress Network, int Prefix)[] BlockedV4 =
    [
        (IPAddress.Parse("0.0.0.0"), 8),
        (IPAddress.Parse("10.0.0.0"), 8),
        (IPAddress.Parse("100.64.0.0"), 10),
        (IPAddress.Parse("127.0.0.0"), 8),
        (IPAddress.Parse("169.254.0.0"), 16),
        (IPAddress.Parse("172.16.0.0"), 12),
        (IPAddress.Parse("192.0.0.0"), 24),
        (IPAddress.Parse("192.0.2.0"), 24),
        (IPAddress.Parse("192.88.99.0"), 24),
        (IPAddress.Parse("192.168.0.0"), 16),
        (IPAddress.Parse("198.18.0.0"), 15),
        (IPAddress.Parse("198.51.100.0"), 24),
        (IPAddress.Parse("203.0.113.0"), 24),
        (IPAddress.Parse("224.0.0.0"), 4),
        (IPAddress.Parse("240.0.0.0"), 4),
    ];

    private static readonly (IPAddress Network, int Prefix)[] BlockedV6 =
    [
        (IPAddress.Parse("::"), 128),
        (IPAddress.Parse("::1"), 128),
        (IPAddress.Parse("::ffff:0:0"), 96),      // IPv4-mapped
        (IPAddress.Parse("::ffff:0:0:0"), 96),    // IPv4-translated
        (IPAddress.Parse("64:ff9b::"), 96),       // NAT64
        (IPAddress.Parse("64:ff9b:1::"), 48),
        (IPAddress.Parse("100::"), 64),           // discard
        (IPAddress.Parse("2001::"), 32),          // Teredo
        (IPAddress.Parse("2001:2::"), 48),        // benchmarking
        (IPAddress.Parse("2001:db8::"), 32),      // documentation
        (IPAddress.Parse("2002::"), 16),          // 6to4
        (IPAddress.Parse("fc00::"), 7),           // unique local (incl. fd00:ec2::254 metadata)
        (IPAddress.Parse("fe80::"), 10),          // link-local
        (IPAddress.Parse("fec0::"), 10),          // site-local (deprecated)
        (IPAddress.Parse("ff00::"), 8),           // multicast
    ];

    public static bool IsPubliclyRoutable(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                return false;
            }

            // Only global unicast 2000::/3 is acceptable.
            if ((address.GetAddressBytes()[0] & 0xE0) != 0x20)
            {
                return false;
            }

            return !BlockedV6.Any(n => InRange(address, n.Network, n.Prefix));
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !address.Equals(IPAddress.Broadcast) && !BlockedV4.Any(n => InRange(address, n.Network, n.Prefix));
        }

        return false;
    }

    /// <summary>All addresses must be public; a single private answer rejects the target (mixed A/AAAA answers).</summary>
    public static bool AllPubliclyRoutable(IReadOnlyCollection<IPAddress> addresses) => addresses.Count > 0 && addresses.All(IsPubliclyRoutable);

    private static bool InRange(IPAddress address, IPAddress network, int prefix)
    {
        if (address.AddressFamily != network.AddressFamily)
        {
            return false;
        }

        var a = address.GetAddressBytes();
        var n = network.GetAddressBytes();
        var full = prefix / 8;
        for (var i = 0; i < full; i++)
        {
            if (a[i] != n[i])
            {
                return false;
            }
        }

        var rem = prefix % 8;
        if (rem == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - rem));
        return (a[full] & mask) == (n[full] & mask);
    }
}
