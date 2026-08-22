using System.Net;
using OneCode.Infrastructure;

namespace OneCode.App.Tools;

/// <summary>
/// URL validation and SSRF protection helpers for WebFetchTool.
/// All methods are private static — no instance state required.
/// </summary>
public sealed partial class WebFetchTool
{
    private static bool ValidateUrl(string url)
    {
        if (url.Length > MaxUrlLength) return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        if (uri.Scheme != "http" && uri.Scheme != "https") return false;

        // Block URLs with credentials
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;

        var host = uri.Host;
        if (string.IsNullOrEmpty(host)) return false;

        // Block localhost by name
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;

        // Block hostname patterns commonly used for internal services
        if (host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return false;

        // Try to parse host as IP address (covers both IPv4 and IPv6)
        if (IPAddress.TryParse(host, out var ipAddress))
        {
            // Block any private/loopback/link-local IP address
            if (IsPrivateOrLocalAddress(ipAddress))
                return false;
        }
        else
        {
            // Hostname (not an IP) — block known private hostname prefixes
            var parts = host.Split('.');
            if (parts.Length < 2) return false;

            // Block private IPv4 ranges by prefix (covers cases where DNS resolves to dotted-decimal hostname)
            if (host.StartsWith("192.168.", StringComparison.OrdinalIgnoreCase) ||
                host.StartsWith("10.", StringComparison.OrdinalIgnoreCase))
                return false;

            // Block 172.16.0.0 – 172.31.255.255 precisely (not all 172.x)
            if (host.StartsWith("172.", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
            {
                if (int.TryParse(parts[1], out var secondOctet) && secondOctet >= 16 && secondOctet <= 31)
                    return false;
            }

            // Block 169.254.x.x (link-local / cloud metadata service)
            if (host.StartsWith("169.254.", StringComparison.OrdinalIgnoreCase))
                return false;

            // Block 0.0.0.0
            if (host == "0.0.0.0")
                return false;
        }

        return true;
    }

    private static bool IsPermittedRedirect(string originalUrl, string redirectUrl)
    {
        if (!Uri.TryCreate(originalUrl, UriKind.Absolute, out var original) ||
            !Uri.TryCreate(redirectUrl, UriKind.Absolute, out var redirect))
        {
            return false;
        }

        if (original.Scheme != redirect.Scheme) return false;
        if (original.Port != redirect.Port) return false;
        if (!string.IsNullOrEmpty(redirect.UserInfo)) return false;

        // Allow www. variations
        var stripWww = new Func<string, string>(h => h.StartsWith("www.", StringComparison.Ordinal) ? h[4..] : h);
        return stripWww(original.Host) == stripWww(redirect.Host);
    }

    /// <summary>
    /// Resolves <paramref name="url"/> and rejects hosts that have any private/loopback
    /// A/AAAA record. Done before HttpClient runs so DNS rebinding is caught without a
    /// ConnectCallback (which would see the local HTTP proxy as the TCP target).
    /// Literal IPs are skipped — <see cref="ValidateUrl"/> already classified them.
    ///
    /// Boundary note: when the URL is routed through an HTTP(S) proxy, the proxy resolves
    /// the hostname server-side, so this client-side lookup is defense-in-depth rather than
    /// the connection path. That means a host reachable only via the proxy's own DNS cannot
    /// be verified here — a known architectural limit; <see cref="ValidateUrl"/> name-based
    /// blocks remain the primary net.
    /// </summary>
    /// <returns>An error message to return to the model, or <c>null</c> when the host is safe.</returns>
    private async Task<string?> GetDnsRebindingBlockReasonAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        if (IPAddress.TryParse(uri.Host, out _))
            return null;

        // A proxied request's hostname is resolved by the proxy, not here, so a local
        // DNS failure must not hard-block legitimate fetches. Only fail closed when the
        // client would resolve and connect directly.
        var viaProxy = ResolvesViaProxy(ProxyConfigService.GetProxyUrl(), url);

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!viaProxy)
            {
                _logger.LogDebug(ex, "DNS lookup failed for WebFetch host {Host}", uri.Host);
                return $"Could not resolve host '{uri.Host}'";
            }

            _logger.LogDebug(ex, "DNS lookup failed for proxied WebFetch host {Host}; allowing (proxy resolves hostname)", uri.Host);
            return null;
        }

        if (addresses.Length == 0 && !viaProxy)
            return $"Could not resolve host '{uri.Host}'";

        var unsafeAddress = FindUnsafeResolvedAddress(addresses);
        if (unsafeAddress is null)
            return null;

        return $"SSRF protection: host '{uri.Host}' resolved to private address {unsafeAddress}";
    }

    /// <summary>
    /// True when a request to <paramref name="url"/> will be forwarded through a proxy, in
    /// which case the hostname is resolved proxy-side and a local DNS failure is non-fatal.
    /// Pure function (proxy URL and NO_PROXY passed in) so the policy can be unit-tested
    /// without mutating process-wide environment variables.
    /// </summary>
    internal static bool ResolvesViaProxy(string? proxyUrl, string url, string? noProxyList = null)
        => !string.IsNullOrWhiteSpace(proxyUrl)
           && !ProxyConfigService.ShouldBypassProxy(url, noProxyList);

    /// <summary>
    /// Returns the first private/loopback/link-local address in <paramref name="addresses"/>,
    /// or <c>null</c> when every record is public. Conservative: one private record is enough
    /// to treat the hostname as unsafe (typical DNS-rebinding shape).
    /// </summary>
    internal static IPAddress? FindUnsafeResolvedAddress(IReadOnlyList<IPAddress> addresses)
    {
        foreach (var address in addresses)
        {
            if (IsPrivateOrLocalAddress(address))
                return address;
        }

        return null;
    }

    /// <summary>
    /// Determines whether an IP address is private, loopback, link-local, or otherwise
    /// unsafe for outbound fetches. Covers IPv4 and IPv6 ranges to prevent SSRF attacks.
    /// Exposed as internal so unit tests can cover the same private-range checks used by ValidateUrl.
    /// </summary>
    internal static bool IsPrivateOrLocalAddressPublic(IPAddress address)
    {
        return IsPrivateOrLocalAddress(address);
    }

    private static bool IsPrivateOrLocalAddress(IPAddress address)
    {
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            // IPv4
            var bytes = address.GetAddressBytes();
            if (bytes.Length != 4) return true; // defensive: treat malformed as private

            // 0.0.0.0/8 — "this network"
            if (bytes[0] == 0) return true;
            // 10.0.0.0/8 — private
            if (bytes[0] == 10) return true;
            // 127.0.0.0/8 — loopback
            if (bytes[0] == 127) return true;
            // 169.254.0.0/16 — link-local (cloud metadata service)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // 172.16.0.0/12 — private
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16 — private
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 100.64.0.0/10 — CGNAT (shared address space, treat as private)
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;

            return false;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            // IPv6
            // ::1 — loopback
            if (IPAddress.IsLoopback(address)) return true;
            if (address.IsIPv6LinkLocal) return true;   // fe80::/10
            if (address.IsIPv6SiteLocal) return true;   // fec0::/10 (deprecated but still block)
            if (address.IsIPv6UniqueLocal) return true; // fc00::/7

            // :: (unspecified address)
            if (address.Equals(IPAddress.IPv6None)) return true;

            // IPv4-mapped IPv6 addresses (::ffff:a.b.c.d) — check the embedded IPv4
            if (address.IsIPv4MappedToIPv6)
            {
                var mapped = address.MapToIPv4();
                return IsPrivateOrLocalAddress(mapped);
            }

            return false;
        }

        // Unknown address family — treat as private for safety
        return true;
    }
}
