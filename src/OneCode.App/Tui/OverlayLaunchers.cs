using OneCode.Core.Config;
using OneCode.App.Session;

namespace OneCode.App.Tui;

/// <summary>
/// Overlay launchers — static helpers to show common TUI overlays.
/// </summary>
public static class OverlayLaunchers
{
    public static async Task<string?> ShowResumeChooserAsync(
        Action<View> pushOverlay,
        Action popOverlay,
        ISessionManager sessionManager,
        CancellationToken ct = default)
    {
        var sessions = await sessionManager.ListAsync(ct).ConfigureAwait(false);
        if (sessions.Count == 0)
            return null;

        var entries = sessions.Select(s => new SessionEntry(
            s.Id.ToString(),
            s.Name,
            s.Model,
            s.Mode ?? "build",
            s.MessageCount,
            s.LastActivityAt)).ToList();

        var overlay = new ResumeChooserOverlay(entries);
        return await overlay.ShowAsync(pushOverlay, popOverlay, ct).ConfigureAwait(false);
    }

    public static async Task<SettingsResult?> ShowSettingsOverlayAsync(
        Action<View> pushOverlay,
        Action popOverlay,
        ConfigSnapshot snapshot,
        bool projectScopeAvailable,
        CancellationToken ct = default)
    {
        var overlay = new SettingsOverlay(snapshot, projectScopeAvailable);
        return await overlay.ShowAsync(pushOverlay, popOverlay, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 打开 MCP 工具白名单配置页。返回 null 表示用户取消；
    /// 非 null 为保存摘要（已由调用方完成配置写回与热生效）。
    /// </summary>
    public static async Task<McpConfigResult?> ShowMcpConfigOverlayAsync(
        Action<View> pushOverlay,
        Action popOverlay,
        IReadOnlyList<McpConfigServerEntry> entries,
        CancellationToken ct = default)
    {
        var overlay = new McpConfigOverlay(entries);
        return await overlay.ShowAsync(pushOverlay, popOverlay, ct).ConfigureAwait(false);
    }
}
