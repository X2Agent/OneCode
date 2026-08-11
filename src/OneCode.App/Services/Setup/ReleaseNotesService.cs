using OneCode.Core.Product;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Services.Setup;

/// <summary>
/// Checks for product updates by querying the latest GitHub release and comparing
/// against the current assembly version.
/// </summary>
public sealed class ReleaseNotesService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ReleaseNotesService> _logger;

    public ReleaseNotesService(IHttpClientFactory httpClientFactory, ILogger<ReleaseNotesService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Query the latest release and compare it with the current version.
    /// <see cref="VersionCheckResult.LatestVersion"/> is <c>null</c> when the check failed.
    /// </summary>
    public async Task<VersionCheckResult> CheckLatestVersionAsync(CancellationToken ct = default)
    {
        var currentVersion = GetCurrentVersion();
        try
        {
            // CreateClient returns an HttpClient bound to the current pooled handler.
            // Do not dispose it — the factory owns the handler lifetime.
            var http = _httpClientFactory.CreateClient(Constants.HttpClientNames.Upgrade);
            var response = await http.GetStringAsync(
                ProductInfo.Default.Repository.LatestReleaseApiUrl, ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(response);
            var latestTag = doc.RootElement.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
            if (latestTag is null)
                return new VersionCheckResult(false, currentVersion, null, null);

            var latestVersion = latestTag.TrimStart('v');
            var htmlUrl = doc.RootElement.TryGetProperty("html_url", out var url) ? url.GetString() : null;
            var isNewer = CompareVersions(latestVersion, currentVersion) > 0;
            return new VersionCheckResult(isNewer, currentVersion, latestVersion, htmlUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check for latest version");
            return new VersionCheckResult(false, currentVersion, null, null);
        }
    }

    private static string GetCurrentVersion() =>
        typeof(ReleaseNotesService).Assembly.GetName().Version?.ToString(3)
            ?? ProductInfo.Default.Version;

    private static int CompareVersions(string v1, string v2)
    {
        var parts1 = v1.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var parts2 = v2.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var maxLen = Math.Max(parts1.Length, parts2.Length);
        for (var i = 0; i < maxLen; i++)
        {
            var p1 = i < parts1.Length ? parts1[i] : 0;
            var p2 = i < parts2.Length ? parts2[i] : 0;
            if (p1 != p2) return p1.CompareTo(p2);
        }
        return 0;
    }
}

public sealed record VersionCheckResult(
    bool IsUpdateAvailable,
    string? CurrentVersion,
    string? LatestVersion,
    string? DownloadUrl);
