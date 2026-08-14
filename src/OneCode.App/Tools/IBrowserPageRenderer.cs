namespace OneCode.App.Tools;

/// <summary>
/// Narrow browser page renderer for WebFetch SPA fallback.
/// Implementations must not depend on <see cref="IToolCatalog"/> (avoids DI cycles).
/// Returns null when rendering is unavailable; callers keep the HTTP→Markdown result.
/// </summary>
public interface IBrowserPageRenderer
{
    /// <summary>
    /// Render <paramref name="url"/> after JavaScript execution and return page text.
    /// Null means skip (not connected, tool missing, or render failed).
    /// </summary>
    Task<string?> RenderAsync(string url, int timeoutMs = 30_000, CancellationToken ct = default);
}
