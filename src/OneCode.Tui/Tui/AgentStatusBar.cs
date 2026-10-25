using OneCode.Core.Mcp;

namespace OneCode.Tui;

/// <summary>
/// Unified agent status — 1 row:
///   ⠋ 思考中 · Opus · 🔒 Sandbox · LSP: 2s · MCP: 3s      BUILD
///
/// Owns all persistent agent runtime and orientation state: activity/configuration
/// on the left, working mode orientation on the right.
/// 自带无进展 watchdog：busy 期间距最近一次事件/活动超过 <see cref="StallWarnAfterSeconds"/>
/// 秒时，activity 被替换为"无进展 Ns"提示（不自动取消——慢本地模型/长工具是合法状态）。
/// </summary>
public sealed class AgentStatusBar : View
{
    /// <summary>Activity 文案的最大显示宽度（列）。超出即截断，避免挤占模型名与状态段。</summary>
    internal const int ActivityMaxWidth = 24;

    /// <summary>距最近一次进展超过该秒数即显示"无进展"提示。</summary>
    internal const double StallWarnAfterSeconds = 45;

    private static readonly TimeSpan StallTickInterval = TimeSpan.FromSeconds(5);

    private readonly IApplication _app;
    private readonly SpinnerController _spinner;
    private readonly WorkingModeController _modeController;
    private bool _busy;
    private bool _modeFlash;
    private object? _modeFlashTimer;
    private object? _stallTimer;
    private string? _stallOverride;
    private long _lastProgressTick = Stopwatch.GetTimestamp();
    private bool _awaitingTool;
    private string _activity = "处理中";
    private string _model = "Opus";
    private readonly string _sandbox = "Sandbox";
    private int _lspServerCount;
    private int _lspErrorCount;
    private int _lspWarningCount;
    private McpConnectionSummary? _mcpSummary;
    private int _activeSubagentCount;

    public AgentStatusBar(IApplication app, WorkingModeController modeController)
    {
        _app = app;
        _spinner = new SpinnerController(app, SetNeedsDraw);
        _modeController = modeController;
        _modeController.ModeChanged += (_, _) =>
        {
            if (_modeFlashTimer is not null)
            {
                app.RemoveTimeout(_modeFlashTimer);
                _modeFlashTimer = null;
            }

            _modeFlash = true;
            SetNeedsDraw();
            _modeFlashTimer = app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
            {
                _modeFlash = false;
                _modeFlashTimer = null;
                SetNeedsDraw();
                return false;
            });
        };

        CanFocus = false;
        Width = Dim.Fill();
        Height = 1;
    }

    public bool IsBusy => _busy;
    public string CurrentActivity => _activity;

    public void SetBusy(bool busy)
    {
        _busy = busy;
        if (busy)
        {
            _spinner.Start();
            StartStallWatchdog();
        }
        else
        {
            _spinner.Stop();
            StopStallWatchdog();
        }
        SetNeedsDraw();
    }

    public void SetActivity(string activity)
    {
        if (string.IsNullOrWhiteSpace(activity) || _activity == activity) return;
        _activity = activity;
        _stallOverride = null;
        _lastProgressTick = Stopwatch.GetTimestamp();
        SetNeedsDraw();
    }

    /// <summary>
    /// 记录引擎侧最近一次事件时间（无进展 watchdog 的时间源）。
    /// 所有 TUI 事件都算进展；非 UI 线程可调，仅写字段。
    /// </summary>
    public void NotifyProgress()
    {
        _lastProgressTick = Stopwatch.GetTimestamp();
        _awaitingTool = false;
    }

    /// <summary>工具调用开始后的静默期属于"等工具"，文案与"等模型"区分开。</summary>
    public void NotifyAwaitingTool()
    {
        _lastProgressTick = Stopwatch.GetTimestamp();
        _awaitingTool = true;
    }

    private void StartStallWatchdog()
    {
        _lastProgressTick = Stopwatch.GetTimestamp();
        _awaitingTool = false;
        _stallOverride = null;
        if (_stallTimer is not null) return;
        _stallTimer = _app.AddTimeout(StallTickInterval, OnStallTick);
    }

    private void StopStallWatchdog()
    {
        if (_stallTimer is not null)
        {
            _app.RemoveTimeout(_stallTimer);
            _stallTimer = null;
        }
        _stallOverride = null;
    }

    private bool OnStallTick()
    {
        if (!_busy)
        {
            _stallTimer = null;
            return false;
        }

        var seconds = (Stopwatch.GetTimestamp() - _lastProgressTick) / (double)Stopwatch.Frequency;
        var stallText = seconds >= StallWarnAfterSeconds
            ? $"{(_awaitingTool ? "工具已执行" : "等待模型响应")} {(int)seconds}s · Esc 中断"
            : null;
        if (_stallOverride != stallText)
        {
            _stallOverride = stallText;
            SetNeedsDraw();
        }
        return true;
    }

    public void SetModel(string m) { _model = string.IsNullOrWhiteSpace(m) ? "Opus" : m; SetNeedsDraw(); }

    /// <summary>
    /// Update the LSP status indicator shown in the status bar.
    /// Pass <paramref name="serverCount"/> == 0 to hide the indicator.
    /// </summary>
    public void SetLspStatus(int serverCount, int errors, int warnings)
    {
        _lspServerCount = serverCount;
        _lspErrorCount = errors;
        _lspWarningCount = warnings;
        SetNeedsDraw();
    }

    /// <summary>
    /// Update the MCP status indicator shown in the status bar (tri-state):
    /// 连接中 x/y（含完成度）→ 已连 n · Nt → 失败 k（红）。
    /// Pass <see langword="null"/> (or a summary without activity) to hide the indicator.
    /// </summary>
    public void SetMcpStatus(McpConnectionSummary? summary)
    {
        _mcpSummary = summary;
        SetNeedsDraw();
    }

    /// <summary>
    /// Update the active sub-agent (fork) count shown as an "agents: N" badge.
    /// Pass 0 to hide the indicator. 与 LSP/MCP 指示器同级：窄终端下整条让位。
    /// </summary>
    public void SetSubagentCount(int activeCount)
    {
        _activeSubagentCount = Math.Max(0, activeCount);
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var w = Viewport.Width;
        if (w <= 0) return false;

        Move(0, 0);
        SetAttribute(new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary));
        AddStr(new string(' ', w));

        // Measure right-side Mode Tag first to establish strict boundary
        var (modeTag, rightCol) = MeasureModeTag(w);
        var maxLeftCol = Math.Max(1, rightCol - 2);

        var col = 1;
        foreach (var item in Fit(BuildLeftItems(), maxLeftCol))
        {
            if (item.Separator.Length > 0 && !TryAddSegment(item.Separator, TuiPalette.FgMuted, ref col, maxLeftCol))
                break;
            if (!TryAddSegment(item.Text, item.Fg, ref col, maxLeftCol))
                break;
        }

        DrawModeTag(rightCol, modeTag);
        return true;
    }

    /// <summary>
    /// 左侧信息条目的唯一事实源：绘制（<see cref="OnDrawingContent"/>）与空间裁剪
    /// （<see cref="Fit"/>）共用同一列表，杜绝两套实现各自维护魔数导致的错位。
    /// 列表顺序即绘制顺序，也是空间不足时由后向前丢弃的顺序。
    /// </summary>
    internal List<LeftItem> BuildLeftItems()
    {
        List<LeftItem> items = [];
        // Activity / Spinner（必显，文案按固定显示宽度截断；无进展提示优先于基础 activity；
        // 正常忙碌态用 InProgress 语义色，仅 stall watchdog 触发时用 Warning 色）
        if (_busy)
        {
            var activity = TextWidthHelper.TruncateByWidth(_stallOverride ?? _activity, ActivityMaxWidth);
            var spinnerColor = _stallOverride is not null ? TuiPalette.Warning : TuiPalette.InProgress;
            items.Add(new LeftItem("", $"{_spinner.CurrentFrame} {activity}", spinnerColor, Droppable: false));
        }

        // Model name（必显，原样显示；超宽由 TryAddSegment 按显示宽度截断兜底）
        items.Add(new LeftItem(items.Count == 0 ? "" : " · ", _model, TuiPalette.FgSecondary, Droppable: false));

        // 以下均为可丢弃条目：空间不足时从尾部整条移除。
        // 常规空闲状态收敛为 FgSecondary/FgMuted，仅在告警或异常时点亮语义色。
        if (_sandbox != "Normal")
            items.Add(new LeftItem(" · ", $"\U0001f512 {_sandbox}", TuiPalette.FgSecondary, Droppable: true));

        // 活跃子代理徽标：与 LSP/MCP 指示器同级行为（窄终端整条让位），N=0 隐藏。
        if (_activeSubagentCount > 0)
            items.Add(new LeftItem(" · ", $"agents: {_activeSubagentCount}", TuiPalette.FgSecondary, Droppable: true));

        if (_lspServerCount > 0)
        {
            var serverColor = _lspErrorCount > 0
                ? TuiPalette.Error
                : (_lspWarningCount > 0 ? TuiPalette.Warning : TuiPalette.FgSecondary);
            items.Add(new LeftItem(" · ", $"LSP: {_lspServerCount}s", serverColor, Droppable: true));

            if (_lspWarningCount > 0)
                items.Add(new LeftItem(" · ", $"\u26a0 {_lspWarningCount}", TuiPalette.Warning, Droppable: true));
            if (_lspErrorCount > 0)
                items.Add(new LeftItem(" · ", $"\u2717 {_lspErrorCount}", TuiPalette.Error, Droppable: true));
        }

        if (_mcpSummary is { HasActivity: true } mcp)
        {
            if (mcp.Connecting > 0)
            {
                var text = mcp.Expected > 0 ? $"MCP: 连接中 {mcp.Connected}/{mcp.Expected}" : "MCP: 连接中";
                items.Add(new LeftItem(" · ", text, TuiPalette.FgSecondary, Droppable: true));
            }
            else if (mcp.Connected > 0)
            {
                items.Add(new LeftItem(" · ", $"MCP: {mcp.Connected}s", TuiPalette.FgSecondary, Droppable: true));
                if (mcp.ToolCount > 0)
                    items.Add(new LeftItem(" · ", $"{mcp.ToolCount}t", TuiPalette.FgMuted, Droppable: true));
            }

            if (mcp.Failed > 0)
            {
                var failColor = mcp.Connected > 0 ? TuiPalette.Warning : TuiPalette.Error;
                var failText = (mcp.Connected > 0 || mcp.Connecting > 0)
                    ? $"\u2717{mcp.Failed}失败"
                    : $"MCP: \u2717{mcp.Failed}失败";
                items.Add(new LeftItem(" · ", failText, failColor, Droppable: true));
            }
        }

        return items;
    }

    /// <summary>
    /// 空间不足时从尾部丢弃可丢弃条目，直到总显示宽度不超过 <paramref name="maxWidth"/>。
    /// 必显条目（<see cref="LeftItem.Droppable"/> = false）永不丢弃，剩余超宽交给
    /// <see cref="TryAddSegment"/> 按列截断。
    /// </summary>
    internal static List<LeftItem> Fit(IReadOnlyList<LeftItem> items, int maxWidth)
    {
        List<LeftItem> result = [.. items];
        while (MeasureWidth(result) > maxWidth)
        {
            var lastDroppable = -1;
            for (var i = result.Count - 1; i >= 0; i--)
            {
                if (result[i].Droppable)
                {
                    lastDroppable = i;
                    break;
                }
            }
            if (lastDroppable < 0) break;
            result.RemoveAt(lastDroppable);
        }
        return result;
    }

    /// <summary>
    /// 条目总显示宽度（含各自分隔符与起始列偏移）。与绘制共用同一列表，
    /// 宽度全部由 <see cref="TextWidthHelper"/> 实测，无独立魔数。
    /// </summary>
    internal static int MeasureWidth(IReadOnlyList<LeftItem> items)
    {
        var width = 1; // 起始列 col = 1
        foreach (var item in items)
            width += TextWidthHelper.GetDisplayWidth(item.Separator) + TextWidthHelper.GetDisplayWidth(item.Text);
        return width;
    }

    private bool TryAddSegment(string text, Color fg, ref int col, int maxCol)
    {
        if (string.IsNullOrEmpty(text)) return true;
        var width = TextWidthHelper.GetDisplayWidth(text);
        if (col + width > maxCol)
        {
            if (maxCol - col > 2)
            {
                var truncated = TextWidthHelper.TruncateByWidth(text, maxCol - col);
                if (!string.IsNullOrEmpty(truncated))
                {
                    Move(col, 0);
                    SetAttribute(new Attribute(fg, TuiPalette.BgPrimary));
                    AddStr(truncated);
                    col += TextWidthHelper.GetDisplayWidth(truncated);
                }
            }
            return false;
        }

        Move(col, 0);
        SetAttribute(new Attribute(fg, TuiPalette.BgPrimary));
        AddStr(text);
        col += width;
        return true;
    }

    private (string ModeTag, int RightCol) MeasureModeTag(int width)
    {
        var modeTag = _modeController.ModeTag;
        var rightWidth = TextWidthHelper.GetDisplayWidth(modeTag) + 1;
        var rightCol = Math.Max(1, width - rightWidth);
        return (modeTag, rightCol);
    }

    private void DrawModeTag(int rightCol, string modeTag)
    {
        Move(rightCol, 0);
        var modeColor = _modeController.Mode switch
        {
            WorkingMode.Build => TuiPalette.ModeBuildFg,
            WorkingMode.Plan => TuiPalette.ModePlanFg,
            WorkingMode.Goal => TuiPalette.ModeGoalFg,
            _ => TuiPalette.FgSecondary,
        };
        SetAttribute(_modeFlash
            ? new Attribute(modeColor, TuiPalette.BgPrimary)
            : new Attribute(TuiPalette.BgPrimary, modeColor));
        AddStr(modeTag);
    }

    /// <summary>
    /// 左侧一个逻辑条目：可选分隔符 + 正文。
    /// <paramref name="Droppable"/> 为 true 时，空间不足会从列表尾部整条丢弃（连同其分隔符）。
    /// </summary>
    internal readonly record struct LeftItem(string Separator, string Text, Color Fg, bool Droppable);
}
