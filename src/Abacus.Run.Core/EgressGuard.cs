using System.Net;

namespace Abacus.Run.Core;

public sealed class EgressBlockedException : Exception
{
    public string Target { get; }

    public EgressBlockedException(string target, string reason)
        : base($"Egress to '{target}' blocked: {reason}") => Target = target;
}

/// <summary>
/// SSRF defense for outbound executor calls. Denies anything not on the allowlist, plus loopback,
/// link-local, and private ranges — an allowlisted DNS name that resolves inward is still blocked.
/// </summary>
public static class EgressGuard
{
    public static void Assert(string url, IReadOnlyCollection<string> allowedHosts, bool enforce = true)
    {
        if (!enforce)
        {
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            throw new EgressBlockedException(url, "not an absolute URI");
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            throw new EgressBlockedException(url, $"scheme '{uri.Scheme}' is not permitted");
        }

        if (IPAddress.TryParse(uri.Host, out IPAddress? literal) && IsInternal(literal))
        {
            throw new EgressBlockedException(url, "resolves to an internal address");
        }

        if (allowedHosts.Count == 0)
        {
            throw new EgressBlockedException(url, "no egress allowlist configured");
        }

        bool allowed = allowedHosts.Any(h => MatchesHost(uri.Host, h));
        if (!allowed)
        {
            throw new EgressBlockedException(url, "host is not on the egress allowlist");
        }
    }

    internal static bool MatchesHost(string host, string pattern)
    {
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            string suffix = pattern[1..];   // ".example.com"
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsInternal(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] switch
            {
                10 => true,
                127 => true,
                169 when b[1] == 254 => true,          // link-local / cloud metadata
                172 when b[1] >= 16 && b[1] <= 31 => true,
                192 when b[1] == 168 => true,
                0 => true,
                _ => false
            };
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
               address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.IPv6Any);
    }
}
