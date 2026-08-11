using OneCode.App.Session;
using OneCode.Infrastructure.Config;

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
}
