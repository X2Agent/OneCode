using System.Net;
using CoreConstants = OneCode.Core.Constants;

namespace OneCode.Infrastructure;

/// <summary>
/// Detects and parses HTTP/HTTPS proxy configuration from standard
/// environment variables: HTTPS_PROXY, HTTP_PROXY, NO_PROXY.
///
/// Respects the same conventions as curl, node.js, and most HTTP tooling:
/// lowercase variants take precedence over uppercase.
/// </summary>
public static class ProxyConfigService
{
    private const string HttpsProxyLower = CoreConstants.EnvVars.HttpsProxyLower;
    private const string HttpsProxyUpper = CoreConstants.EnvVars.HttpsProxy;
    private const string HttpProxyLower = CoreConstants.EnvVars.HttpProxyLower;
    private const string HttpProxyUpper = CoreConstants.EnvVars.HttpProxy;
    private const string NoProxyLower = CoreConstants.EnvVars.NoProxyLower;
    private const string NoProxyUpper = CoreConstants.EnvVars.NoProxy;

    /// <summary>
    /// Get the active proxy URL, preferring lower-case env vars.
    /// Returns null if no proxy is configured.
    /// </summary>
    public static string? GetProxyUrl()
    {
        return Environment.GetEnvironmentVariable(HttpsProxyLower)
            ?? Environment.GetEnvironmentVariable(HttpsProxyUpper)
            ?? Environment.GetEnvironmentVariable(HttpProxyLower)
            ?? Environment.GetEnvironmentVariable(HttpProxyUpper);
    }

    /// <summary>
    /// Get the NO_PROXY bypass list (comma/space separated host patterns).
    /// Returns null if not configured.
    /// </summary>
    public static string? GetNoProxyList()
    {
        return Environment.GetEnvironmentVariable(NoProxyLower)
            ?? Environment.GetEnvironmentVariable(NoProxyUpper);
    }

    /// <summary>
    /// Check whether a given URL should bypass the proxy based on NO_PROXY rules.
    /// Supports:
    /// - Exact hostname matches (e.g., "localhost")
    /// - Domain suffix matches with leading dot (e.g., ".example.com")
    /// - Wildcard "*" to bypass all
    /// - Port-specific matches (e.g., "example.com:8080")
    /// - IP addresses (e.g., "127.0.0.1")
    /// </summary>
    /// <param name="urlString">The URL to check.</param>
    /// <param name="noProxyList">
    /// The NO_PROXY value. If null, uses <see cref="GetNoProxyList"/>.
    /// </param>
    public static bool ShouldBypassProxy(string urlString, string? noProxyList = null)
    {
        noProxyList ??= GetNoProxyList();
        if (string.IsNullOrWhiteSpace(noProxyList))
            return false;

        // Wildcard: bypass everything
        if (noProxyList.Trim() == "*")
            return true;

        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri))
            return false;

        var hostname = uri.Host.ToLowerInvariant();
        var port = uri.Port > 0 ? uri.Port : (uri.Scheme == "https" ? 443 : 80);
        var hostWithPort = $"{hostname}:{port}";

        var patterns = noProxyList.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var raw in patterns)
        {
            var pattern = raw.Trim().ToLowerInvariant();
            if (pattern.Length == 0) continue;

            // Port-specific match: "example.com:8080"
            if (pattern.Contains(':'))
            {
                if (hostWithPort == pattern)
                    return true;
                continue;
            }

            // Domain suffix match: ".example.com" matches "sub.example.com" and "example.com"
            if (pattern.StartsWith('.'))
            {
                if (hostname == pattern[1..] || hostname.EndsWith(pattern, StringComparison.Ordinal))
                    return true;
                continue;
            }

            // Exact hostname match
            if (hostname == pattern)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Parse a proxy URL and create a <see cref="WebProxy"/> instance.
    /// Supports http:// and socks5:// schemes, and user:pass@ authentication.
    /// Returns null if proxyUrl is null/empty.
    /// </summary>
    public static WebProxy? CreateProxy(string? proxyUrl, string? noProxyList = null)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl))
            return null;

        if (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri))
            return null;

        ICredentials? credentials = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            credentials = new NetworkCredential(
                Uri.UnescapeDataString(parts[0]),
                parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
        }

        var proxy = new WebProxy
        {
            Address = uri,
            Credentials = credentials,
            BypassProxyOnLocal = true,
        };

        noProxyList ??= GetNoProxyList();
        if (!string.IsNullOrWhiteSpace(noProxyList) && noProxyList.Trim() != "*")
        {
            proxy.BypassList = noProxyList
                .Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToArray();
        }

        return proxy;
    }

    /// <summary>
    /// Configure an <see cref="HttpClientHandler"/> with proxy settings
    /// from environment variables. Also applies mTLS if configured.
    /// </summary>
    public static void ApplyToHandler(HttpClientHandler handler)
    {
        var proxyUrl = GetProxyUrl();
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            handler.Proxy = CreateProxy(proxyUrl);
            handler.UseProxy = true;
        }

        // Apply mTLS (preserves existing behavior)
        MtlsHelper.ApplyToHandler(handler);
    }

    /// <summary>
    /// Get the proxy URL for WebSocket connections (same as HTTP proxy).
    /// Returns null if no proxy or URL should bypass.
    /// </summary>
    public static string? GetWebSocketProxyUrl(string urlString)
    {
        var proxyUrl = GetProxyUrl();
        if (string.IsNullOrWhiteSpace(proxyUrl))
            return null;

        if (ShouldBypassProxy(urlString))
            return null;

        return proxyUrl;
    }
}
