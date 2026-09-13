using OneCode.Core.Mcp;

namespace OneCode.App.Tui;

/// <summary>
/// Unified agent status — 1 row:
///   ⠋ 思考中 · Opus · $0.04 · 🔒 Sandbox · LSP: 2s      BUILD
///
/// Owns all persistent agent runtime and orientation state: activity/configuration
/// on the left, working mode/team orientation on the right.
/// </summary>
public sealed class AgentStatusBar : View
{
    /// <summary>Activity 文案的最大显示宽度（列）。超出即截断，避免挤占模型名与状态段。</summary>
    internal const int ActivityMaxWidth = 24;

    private readonly SpinnerController _spinner;
    private readonly WorkingModeController _modeController;
    private bool _busy;
    private bool _modeFlash;
    private object? _modeFlashTimer;
    private string? _activeTeam;
    private string? _teamModeLabel;
    private string _activity = "处理中";
    private string _model = "Opus";
    private readonly string _sandbox = "Sandbox";
    private int _lspServerCount;
    private int _lspErrorCount;
    private int _lspWarningCount;
    private McpConnectionSummary? _mcpSummary;

    public AgentStatusBar(IApplication app, WorkingModeController modeController)
    {
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
            _spinner.Start();
        else
            _spinner.Stop();
        SetNeedsDraw();
    }

    public void SetActivity(string activity)
    {
        if (string.IsNullOrWhiteSpace(activity) || _activity == activity) return;
        _activity = activity;
        SetNeedsDraw();
    }

    public void SetModel(string m) { _model = string.IsNullOrWhiteSpace(m) ? "Opus" : m; SetNeedsDraw(); }

    /// <summary>
    /// 更新团队标签。<paramref name="teamModeLabel"/> 为该团队 team.yaml 声明的编排模式
    /// 标签（经 TeamOrchestrationModeExtensions.ToLabel 得到）——模式是团队的固定属性，
    /// 状态栏只透出 YAML 事实，不存在运行期覆盖。
    /// </summary>
    public void SetActiveTeam(string? teamName, string? teamModeLabel = null)
    {
        if (_activeTeam == teamName && _teamModeLabel == teamModeLabel) return;
        _activeTeam = teamName;
        _teamModeLabel = teamModeLabel;
        SetNeedsDraw();
    }

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

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var w = Viewport.Width;
        if (w <= 0) return false;

        Move(0, 0);
        SetAttribute(new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary));
        AddStr(new string(' ', w));

        // Measure right-side Mode Tag first to establish strict boundary
        var (modeTag, strategyLabel, teamLabel, rightCol) = MeasureModeTag(w);
        var maxLeftCol = Math.Max(1, rightCol - 2);

        var col = 1;
        foreach (var item in Fit(BuildLeftItems(), maxLeftCol))
        {
            if (item.Separator.Length > 0 && !TryAddSegment(item.Separator, TuiPalette.FgMuted, ref col, maxLeftCol))
                break;
            if (!TryAddSegment(item.Text, item.Fg, ref col, maxLeftCol))
                break;
        }

        DrawModeTag(rightCol, modeTag, strategyLabel, teamLabel);
        return true;
    }

    /// <summary>
    /// 左侧信息条目的唯一事实源：绘制（<see cref="OnDrawingContent"/>）与空间裁剪
    /// （<see cref="Fit"/>）共用同一列表，杜绝两套实现各自维护魔数导致的错位。
    /// 列表顺序即绘制顺序，也是空间不足时由后向前丢弃的顺序。
    /// </summary>
    internal List<LeftItem> BuildLeftItems()
    {
        var items = new List<LeftItem>();
        // Activity / Spinner（必显，文案按固定显示宽度截断）
        if (_busy)
        {
            var activity = TextWidthHelper.TruncateByWidth(_activity, ActivityMaxWidth);
            items.Add(new LeftItem("", $"{_spinner.CurrentFrame} {activity}", TuiPalette.Warning, Droppable: false));
        }

        // Model name（必显，原样显示；超宽由 TryAddSegment 按显示宽度截断兜底）
        items.Add(new LeftItem(items.Count == 0 ? "" : " · ", _model, TuiPalette.FgPrimary, Droppable: false));

        // 以下均为可丢弃条目：空间不足时从尾部整条移除。
        if (_sandbox != "Normal")
            items.Add(new LeftItem(" · ", $"\U0001f512 {_sandbox}", TuiPalette.Info, Droppable: true));

        if (_lspServerCount > 0)
        {
            var serverColor = _lspErrorCount > 0
                ? TuiPalette.Error
                : (_lspWarningCount > 0 ? TuiPalette.Warning : TuiPalette.StatusOk);
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
                items.Add(new LeftItem(" · ", text, TuiPalette.Info, Droppable: true));
            }
            else if (mcp.Connected > 0)
            {
                items.Add(new LeftItem(" · ", $"MCP: {mcp.Connected}s", TuiPalette.Info, Droppable: true));
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
        var result = new List<LeftItem>(items);
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

    private (string ModeTag, string StrategyLabel, string TeamLabel, int RightCol) MeasureModeTag(int width)
    {
        var modeTag = _modeController.ModeTag;
        var strategyLabel = _modeController.Mode == WorkingMode.Team && !string.IsNullOrEmpty(_teamModeLabel)
            ? $" \u00b7 {_teamModeLabel}"
            : "";
        var teamLabel = _modeController.Mode == WorkingMode.Team && !string.IsNullOrEmpty(_activeTeam)
            ? $" \u00b7 {_activeTeam}"
            : "";
        var rightWidth = TextWidthHelper.GetDisplayWidth(modeTag)
            + TextWidthHelper.GetDisplayWidth(strategyLabel)
            + TextWidthHelper.GetDisplayWidth(teamLabel) + 1;
        var rightCol = Math.Max(1, width - rightWidth);
        return (modeTag, strategyLabel, teamLabel, rightCol);
    }

    private void DrawModeTag(int rightCol, string modeTag, string strategyLabel, string teamLabel)
    {
        Move(rightCol, 0);
        var modeColor = _modeController.Mode switch
        {
            WorkingMode.Build => TuiPalette.ModeBuildFg,
            WorkingMode.Plan => TuiPalette.ModePlanFg,
            WorkingMode.Team => TuiPalette.ModeTeamFg,
            WorkingMode.Goal => TuiPalette.ModeGoalFg,
            _ => TuiPalette.FgSecondary,
        };
        SetAttribute(_modeFlash
            ? new Attribute(modeColor, TuiPalette.BgPrimary)
            : new Attribute(TuiPalette.BgPrimary, modeColor));
        AddStr(modeTag);

        if (strategyLabel.Length > 0)
        {
            SetAttribute(new Attribute(TuiPalette.FgSecondary, TuiPalette.BgPrimary));
            AddStr(strategyLabel);
        }
        if (teamLabel.Length > 0)
        {
            SetAttribute(new Attribute(TuiPalette.Accent, TuiPalette.BgPrimary));
            AddStr(teamLabel);
        }
    }

    /// <summary>
    /// 左侧一个逻辑条目：可选分隔符 + 正文。
    /// <paramref name="Droppable"/> 为 true 时，空间不足会从列表尾部整条丢弃（连同其分隔符）。
    /// </summary>
    internal readonly record struct LeftItem(string Separator, string Text, Color Fg, bool Droppable);
}
