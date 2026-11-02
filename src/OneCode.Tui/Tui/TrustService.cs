using OneCode.Core.Config;
using OneCode.Core.IO;
using OneCode.Infrastructure;

namespace OneCode.Tui;

/// <summary>
/// 启动时的信任门：必要时弹 <see cref="TrustOverlay"/>，并把用户的选择持久化到
/// <c>settings.json</c> 的 <c>trustedDirectories</c>。列表中的目录及其子目录隐式受信任。
/// </summary>
public sealed class TrustService
{
    private readonly IConfigManager _configManager;
    private Action<View>? _pushOverlay;
    private Action? _popOverlay;
    private bool _sessionTrustAccepted;

    /// <summary>
    /// 预 TUI 启动路径只能对本次会话放行（此时浮层宿主尚未接线），据此标记
    /// <see cref="OneCodeToplevel.ScheduleInitialFocus"/> 在 Terminal.Gui 就绪后补弹确认框。
    /// </summary>
    public bool NeedsPostTuiConfirmation { get; private set; }

    public TrustService(IConfigManager configManager)
    {
        _configManager = configManager;
    }

    /// <summary>
    /// 控制台能否承载交互式确认。测试经 <c>InternalsVisibleTo</c> 替身化该探针，
    /// 否则非交互分支会随测试宿主是否重定向 stdin 而漂移。
    /// </summary>
    internal Func<bool> IsInteractive { get; set; } =
        static () => Environment.UserInteractive && !Console.IsInputRedirected;

    /// <summary>
    /// 注入 TUI 宿主的浮层入栈 / 出栈委托（<c>OneCodeToplevel.PushOverlay</c> /
    /// <c>OneCodeToplevel.PopTopOverlay</c>）。未注入时无法弹浮层，只能会话级放行。
    /// </summary>
    public void SetOverlayDelegates(Action<View> pushOverlay, Action popOverlay)
    {
        _pushOverlay = pushOverlay;
        _popOverlay = popOverlay;
    }

    /// <summary>当前工作目录是否需要先确认信任。</summary>
    public bool ShouldShowTrustPrompt()
    {
        if (_sessionTrustAccepted)
            return false;

        // 全局信任开关：hasTrustAccepted 置位后信任所有目录。
        if (_configManager.Current.Effective.HasTrustAccepted)
            return false;

        var cwd = Path.GetFullPath(Directory.GetCurrentDirectory());
        return !IsDirectoryTrusted(cwd);
    }

    /// <summary>
    /// 清掉会话级放行标记并复位 <see cref="NeedsPostTuiConfirmation"/>，让 TUI 就绪后的
    /// 补弹重新对照已持久化的信任列表求值。
    /// </summary>
    public void ResetSessionTrustForPostTuiCheck()
    {
        _sessionTrustAccepted = false;
        NeedsPostTuiConfirmation = false;
    }

    /// <summary><paramref name="directory"/> 或任一父目录是否已在信任列表中。</summary>
    public bool IsDirectoryTrusted(string directory)
    {
        var normalizedCwd = PathsHelper.NormalizePath(directory);
        var trusted = _configManager.Current.Effective.TrustedDirectories;

        foreach (var trustedDir in trusted)
        {
            if (PathBoundary.IsWithinDirectory(normalizedCwd, trustedDir, PathBoundary.DefaultComparison))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 需要时完成信任确认，返回是否可继续。只有用户在前端界面上确实选了「信任」才写入
    /// <c>trustedDirectories</c>。
    /// </summary>
    /// <remarks>
    /// 两条「仅本次会话放行」路径：非交互控制台、浮层宿主尚未接线。它们只翻会话标记、不落盘。
    /// 一旦落盘，<see cref="IsDirectoryTrusted"/> 立刻命中当前目录，TUI 就绪后的补弹会被
    /// <see cref="ShouldShowTrustPrompt"/> 静默跳过——用户永远看不到确认框，而信任记录已被写入。
    /// </remarks>
    public async Task<bool> EnsureTrustAsync(CancellationToken ct = default)
    {
        if (!ShouldShowTrustPrompt())
            return true;

        var cwd = Directory.GetCurrentDirectory();

        if (!IsInteractive())
        {
            _sessionTrustAccepted = true;
            return true;
        }

        if (_pushOverlay is null || _popOverlay is null)
        {
            // 预 TUI 阶段弹不了浮层；在这里 Console.ReadLine() 会把进程冻死在 TUI 启动之前，
            // 故先会话放行，由 ScheduleInitialFocus 在 Terminal.Gui 就绪后补弹。
            _sessionTrustAccepted = true;
            NeedsPostTuiConfirmation = true;
            return true;
        }

        // 用户主目录不做持久化：把 ~ 写进信任列表等于信任一切。
        // 必须按平台路径比较语义判等：Windows 上 `c:\users\x` 与 `C:\Users\x` 是同一目录，
        // 用 == 会判成「非主目录」而把用户主目录写进信任列表——那等于全局信任。
        var isHomeDir = IsSameDirectory(cwd, PathsHelper.UserHome);
        var accepted = await new TrustOverlay(cwd, isHomeDir)
            .ShowAsync(_pushOverlay, _popOverlay, ct).ConfigureAwait(false);
        if (!accepted)
            return false;

        if (isHomeDir)
        {
            _sessionTrustAccepted = true;
            return true;
        }

        await AddTrustedDirectoryAsync(cwd, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 两个规范化路径是否指向同一目录。Windows 路径大小写不敏感，`c:\users\x` 与
    /// `C:\Users\x` 是同一处；用序数 <c>==</c> 比较会把用户主目录误判成普通目录。
    /// </summary>
    private static bool IsSameDirectory(string left, string right) =>
        string.Equals(
            PathsHelper.NormalizePath(left),
            PathsHelper.NormalizePath(right),
            PathBoundary.DefaultComparison);

    private async Task AddTrustedDirectoryAsync(string directory, CancellationToken ct)
    {
        var normalized = PathsHelper.NormalizePath(directory);
        var trusted = _configManager.Current.Effective.TrustedDirectories;

        if (IsDirectoryTrusted(normalized))
            return;

        // 子目录已在列表中时用父目录取代它，避免列表随启动路径逐层增长。
        trusted.RemoveAll(existing =>
            PathBoundary.IsWithinDirectory(existing, normalized, PathBoundary.DefaultComparison));

        trusted.Add(normalized);
        var result = await _configManager.ApplyAsync(
            ConfigPatch.Set(ConfigScope.User, "trustedDirectories", trusted.ToArray()),
            ct).ConfigureAwait(false);
        if (!result.Saved)
            throw new IOException(result.Error ?? "Failed to save trusted directories.");
    }
}
