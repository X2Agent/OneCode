using OneCode.Infrastructure;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Tui;

/// <summary>
/// Service responsible for showing the trust overlay on startup and persisting the user's choice.
/// Trusted directories are stored as a list. Subdirectories of a trusted directory are implicitly trusted.
/// </summary>
public sealed class TrustService
{
    private readonly IConfigManager _configManager;
    private Action<View>? _pushOverlay;
    private Action? _popOverlay;
    private bool _sessionTrustAccepted;

    /// <summary>
    /// True when the pre-TUI startup path silently accepted trust because
    /// the overlay host was not yet wired up. In that case
    /// <see cref="OneCodeToplevel.ScheduleInitialFocus"/> will re-check and
    /// show the trust overlay after Terminal.Gui is ready.
    /// </summary>
    public bool NeedsPostTuiConfirmation { get; private set; }

    public TrustService(IConfigManager configManager)
    {
        _configManager = configManager;
    }

    /// <summary>
    /// Injects the overlay push/pop delegates from the TUI host
    /// (e.g. <c>OneCodeToplevel.PushOverlay</c> / <c>OneCodeToplevel.PopTopOverlay</c>).
    /// When set, the trust overlay is shown as a CenteredOverlay popup; otherwise
    /// the service falls back to console prompts or defers to post-TUI confirmation.
    /// </summary>
    public void SetOverlayDelegates(Action<View> pushOverlay, Action popOverlay)
    {
        _pushOverlay = pushOverlay;
        _popOverlay = popOverlay;
    }

    /// <summary>
    /// Checks whether the trust overlay needs to be shown for the current working directory.
    /// </summary>
    public bool ShouldShowTrustPrompt()
    {
        if (_sessionTrustAccepted)
            return false;

        // Global trust flag — when set (e.g., via CLI --trust), trust every directory.
        if (_configManager.Current.Effective.HasTrustAccepted)
            return false;

        var cwd = Path.GetFullPath(Directory.GetCurrentDirectory());
        return !IsDirectoryTrusted(cwd);
    }

    /// <summary>
    /// Clears the session-level trust flag and re-evaluates trust so the
    /// post-TUI prompt can show the overlay that was skipped during pre-TUI startup.
    /// </summary>
    public void ResetSessionTrustForPostTuiCheck()
    {
        _sessionTrustAccepted = false;
        NeedsPostTuiConfirmation = false;
    }

    /// <summary>
    /// Checks if <paramref name="directory"/> (or any parent) is in the trusted list.
    /// </summary>
    public bool IsDirectoryTrusted(string directory)
    {
        var normalizedCwd = PathsHelper.NormalizePath(directory);
        var trusted = _configManager.Current.Effective.TrustedDirectories;

        foreach (var trustedDir in trusted)
        {
            var normalizedTrusted = PathsHelper.NormalizePath(trustedDir);
            if (normalizedCwd.Equals(normalizedTrusted, PathComparison)
                || normalizedCwd.StartsWith(normalizedTrusted + Path.DirectorySeparatorChar, PathComparison))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Shows the trust overlay if needed and returns whether the user accepted.
    /// </summary>
    public async Task<bool> EnsureTrustAsync(CancellationToken ct = default)
    {
        if (!ShouldShowTrustPrompt())
            return true;

        var cwd = Directory.GetCurrentDirectory();
        var homeDir = PathsHelper.UserHome;
        var isHomeDir = PathsHelper.NormalizePath(cwd) == PathsHelper.NormalizePath(homeDir);

        bool accepted;

        if (_pushOverlay is not null && _popOverlay is not null && !Console.IsInputRedirected)
        {
            // TUI is ready — show TrustOverlay as a CenteredOverlay popup so the
            // background conversation stays visible (uniform overlay system).
            var overlay = new TrustOverlay(cwd, isHomeDir);
            accepted = await overlay.ShowAsync(_pushOverlay, _popOverlay, ct).ConfigureAwait(false);
        }
        else if (Console.IsInputRedirected || !Environment.UserInteractive)
        {
            // Non-interactive console (CI, piped, redirected). Defer trust decision
            // to the TUI by accepting for this session only — the TUI will re-show
            // the overlay on next user interaction. We don't persist trust here.
            _sessionTrustAccepted = true;
            accepted = true;
        }
        else if (_pushOverlay is null)
        {
            // Pre-TUI startup phase: the overlay host isn't wired up yet
            // (TuiHost.Run is still ahead), so the TrustOverlay cannot be shown.
            // Blocking on Console.ReadLine() here would freeze the process before
            // the TUI ever starts, so accept for this session only. The TUI's
            // on-startup path can re-prompt if needed; we do not persist trust.
            _sessionTrustAccepted = true;
            NeedsPostTuiConfirmation = true;
            accepted = true;
        }
        else
        {
            accepted = await ShowConsolePromptAsync(cwd, ct).ConfigureAwait(false);
        }

        if (accepted)
        {
            if (isHomeDir)
            {
                _sessionTrustAccepted = true;
            }
            else
            {
                await AddTrustedDirectoryAsync(cwd, ct).ConfigureAwait(false);
            }
        }

        return accepted;
    }

    private async Task AddTrustedDirectoryAsync(string directory, CancellationToken ct)
    {
        var normalized = PathsHelper.NormalizePath(directory);
        var trusted = _configManager.Current.Effective.TrustedDirectories;

        if (IsDirectoryTrusted(normalized))
            return;

        trusted.RemoveAll(existing =>
        {
            var normalizedExisting = PathsHelper.NormalizePath(existing);
            return normalizedExisting.StartsWith(normalized + Path.DirectorySeparatorChar, PathComparison);
        });

        trusted.Add(normalized);
        var result = await _configManager.ApplyAsync(
            ConfigPatch.Set(ConfigScope.User, "trustedDirectories", trusted.ToArray()),
            ct).ConfigureAwait(false);
        if (!result.Saved)
            throw new IOException(result.Error ?? "Failed to save trusted directories.");
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static async Task<bool> ShowConsolePromptAsync(string cwd, CancellationToken ct)
    {
        if (Console.IsInputRedirected || !Environment.UserInteractive)
        {
            Console.WriteLine("Warning: Running in non-interactive mode. Trust prompt cannot be shown.");
            Console.WriteLine("Use --yes-trust flag or set trustedDirectories in config to skip this prompt.");
            return false;
        }

        Console.WriteLine();
        Console.WriteLine($"Accessing workspace: {cwd}");
        Console.WriteLine();
        Console.WriteLine("Quick safety check: Is this a project you created or one you trust?");
        Console.WriteLine("(Like your own code, a well-known open source project, or work from your team).");
        Console.WriteLine("If not, take a moment to review what's in this folder first.");
        Console.WriteLine();
        Console.WriteLine("OneCode will be able to read, edit, and execute files here.");
        Console.WriteLine();
        Console.Write("Do you trust this folder? [y/N]: ");

        var line = await Console.In.ReadLineAsync(ct).ConfigureAwait(false);
        var trimmed = line?.Trim().ToLowerInvariant() ?? string.Empty;
        return trimmed is "y" or "yes";
    }
}
