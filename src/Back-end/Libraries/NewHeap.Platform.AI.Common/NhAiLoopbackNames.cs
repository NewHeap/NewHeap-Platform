using System.Net;

namespace NewHeap.Platform.AI;

/// <summary>
/// The one rule for loopback names that the MCP protected resource, the assistant's options and the
/// assistant's outbound guard share: <c>localhost</c>, a name under <c>.localhost</c> (RFC 6761) and a
/// loopback IP literal. Any other name, such as <c>mylocalhost</c> or <c>localhost.example.org</c>, is
/// not a loopback name, whatever it resolves to.
/// </summary>
internal static class NhAiLoopbackNames
{
    private const string Localhost = "localhost";
    private const string LocalhostSuffix = ".localhost";

    /// <summary>Whether the host of an absolute URL is a loopback name.</summary>
    public static bool IsLoopbackName(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsAbsoluteUri && IsLoopbackName(uri.IdnHost);
    }

    /// <summary>
    /// Whether a host name is <c>localhost</c>, a name under <c>.localhost</c> or a loopback IP literal.
    /// A trailing dot and the case of the name do not matter.
    /// </summary>
    public static bool IsLoopbackName(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var name = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (name == Localhost || (name.EndsWith(LocalhostSuffix, StringComparison.Ordinal) && name.Length > LocalhostSuffix.Length))
        {
            return true;
        }

        return IPAddress.TryParse(name, out var address) && IsLoopbackAddress(address);
    }

    /// <summary>
    /// Whether a host name is <c>localhost</c> or a name under <c>.localhost</c>: a name that DNS
    /// resolves, unlike an IP literal.
    /// </summary>
    public static bool IsLocalhostName(string? host)
    {
        return IsLoopbackName(host) && !IPAddress.TryParse(host!.Trim().TrimEnd('.'), out _);
    }

    /// <summary>Whether an address is a loopback address, also as an IPv4-mapped IPv6 address.</summary>
    public static bool IsLoopbackAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address);
    }
}
