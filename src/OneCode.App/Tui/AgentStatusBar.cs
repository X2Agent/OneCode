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

    internal static string ShortenModelName(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "Opus";

        var lower = model.ToLowerInvariant();
        if (lower.Contains("3-7-sonnet") || lower.Contains("3.7-sonnet")) return "Sonnet 3.7";
        if (lower.Contains("3-5-sonnet") || lower.Contains("3.5-sonnet")) return "Sonnet 3.5";
        if (lower.Contains("3-5-haiku") || lower.Contains("3.5-haiku")) return "Haiku 3.5";
        if (lower.Contains("3-opus") || lower.Contains("opus")) return "Opus";
        if (lower.Contains("sonnet-4")) return "Sonnet 4";
        if (lower.Contains("gpt-4o-mini")) return "GPT-4o-mini";
        if (lower.Contains("gpt-4o")) return "GPT-4o";
        if (lower.Contains("gpt-4-turbo")) return "GPT-4T";
        if (lower.Contains("o3-mini")) return "o3-mini";
        if (lower.Contains("o1-mini")) return "o1-mini";
        if (lower.Contains("o1-preview") || lower.Equals("o1", StringComparison.OrdinalIgnoreCase)) return "o1";

        if (model.Length > 12)
            return model[..10] + TuiGlyphs.Ellipsis;

        return model;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var w = Viewport.Width;
        if (w <= 0) return false;

        Move(0, 0);
        SetAttribute(new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary));
        AddStr(new string(' ', w));

        // Measure right-side Mode Tag first to establish strict boundary
        var (modeTag, strategyLabel, teamLabel, rightWidth, rightCol) = MeasureModeTag(w);
        var maxLeftCol = Math.Max(1, rightCol - 2);

        // Determine degradation level (0..4) based on available width
        var level = DetermineDegradationLevel(maxLeftCol);

        var col = 1;

        // Activity / Spinner
        if (_busy)
        {
            var act = level >= 4 && _activity.Length > 6 ? _activity[..5] + TuiGlyphs.Ellipsis : _activity;
            if (!TryAddSegment($"{_spinner.CurrentFrame} ", TuiPalette.Warning, ref col, maxLeftCol)) goto DrawRight;
            if (!TryAddSegment(act, TuiPalette.FgSecondary, ref col, maxLeftCol)) goto DrawRight;
            if (!TryAddSegment(" \u00b7 ", TuiPalette.FgMuted, ref col, maxLeftCol)) goto DrawRight;
        }

        // Model name (shortened if level >= 1)
        var modelToDisplay = level >= 1 ? ShortenModelName(_model) : _model;
        if (!TryAddSegment(modelToDisplay, TuiPalette.FgPrimary, ref col, maxLeftCol)) goto DrawRight;

        // Sandbox (hidden if level >= 4)
        if (_sandbox != "Normal" && level < 4)
        {
            if (!TryAddSegment(" \u00b7 ", TuiPalette.FgMuted, ref col, maxLeftCol)) goto DrawRight;
            if (!TryAddSegment($"\U0001f512 {_sandbox}", TuiPalette.Info, ref col, maxLeftCol)) goto DrawRight;
        }

        // LSP Status (level 0..2: full; level 3: collapsed server count only; level >= 4: hidden)
        if (_lspServerCount > 0 && level < 4)
        {
            if (!TryAddSegment(" \u00b7 ", TuiPalette.FgMuted, ref col, maxLeftCol)) goto DrawRight;

            var serverColor = _lspErrorCount > 0
                ? TuiPalette.Error
                : (_lspWarningCount > 0 ? TuiPalette.Warning : TuiPalette.StatusOk);
            if (!TryAddSegment($"LSP: {_lspServerCount}s", serverColor, ref col, maxLeftCol)) goto DrawRight;

            if (level < 3)
            {
                if (_lspWarningCount > 0)
                {
                    if (!TryAddSegment(" \u00b7 ", TuiPalette.FgMuted, ref col, maxLeftCol)) goto DrawRight;
                    if (!TryAddSegment($"\u26a0 {_lspWarningCount}", TuiPalette.Warning, ref col, maxLeftCol)) goto DrawRight;
                }
                if (_lspErrorCount > 0)
                {
                    if (!TryAddSegment(" \u00b7 ", TuiPalette.FgMuted, ref col, maxLeftCol)) goto DrawRight;
                    if (!TryAddSegment($"\u2717 {_lspErrorCount}", TuiPalette.Error, ref col, maxLeftCol)) goto DrawRight;
                }
            }
        }

        // MCP Status (level 0..1: full; level 2..3: collapsed without tool count; level >= 4: hidden)
        if (_mcpSummary is { HasActivity: true } mcp && level < 4)
        {
            if (!TryAddSegment(" \u00b7 ", TuiPalette.FgMuted, ref col, maxLeftCol)) goto DrawRight;

            if (mcp.Connecting > 0)
            {
                var connectingText = mcp.Expected > 0 ? $"MCP: 连接中 {mcp.Connected}/{mcp.Expected}" : "MCP: 连接中";
                if (!TryAddSegment(connectingText, TuiPalette.Info, ref col, maxLeftCol)) goto DrawRight;
            }
            else if (mcp.Connected > 0)
            {
                if (!TryAddSegment($"MCP: {mcp.Connected}s", TuiPalette.Info, ref col, maxLeftCol)) goto DrawRight;
                if (mcp.ToolCount > 0 && level < 2)
                {
                    if (!TryAddSegment($" \u00b7 {mcp.ToolCount}t", TuiPalette.FgMuted, ref col, maxLeftCol)) goto DrawRight;
                }
            }

            if (mcp.Failed > 0)
            {
                if (!TryAddSegment(" \u00b7 ", TuiPalette.FgMuted, ref col, maxLeftCol)) goto DrawRight;
                var failColor = mcp.Connected > 0 ? TuiPalette.Warning : TuiPalette.Error;
                var failText = (mcp.Connected > 0 || mcp.Connecting > 0)
                    ? $"\u2717{mcp.Failed}失败"
                    : $"MCP: \u2717{mcp.Failed}失败";
                if (!TryAddSegment(failText, failColor, ref col, maxLeftCol)) goto DrawRight;
            }
        }

    DrawRight:
        DrawModeTag(rightCol, modeTag, strategyLabel, teamLabel);
        return true;
    }

    private int DetermineDegradationLevel(int maxLeftCol)
    {
        for (var level = 0; level <= 4; level++)
        {
            if (EstimateLeftWidth(level) <= maxLeftCol)
                return level;
        }
        return 4;
    }

    private int EstimateLeftWidth(int level)
    {
        var width = 1; // initial col = 1

        if (_busy)
        {
            var act = level >= 4 && _activity.Length > 6 ? _activity[..5] + TuiGlyphs.Ellipsis : _activity;
            width += 2 + TextWidthHelper.GetDisplayWidth(act) + 3; // spinner + act + " · "
        }

        var modelToDisplay = level >= 1 ? ShortenModelName(_model) : _model;
        width += TextWidthHelper.GetDisplayWidth(modelToDisplay);

        if (_sandbox != "Normal" && level < 4)
        {
            width += 3 + 2 + 1 + TextWidthHelper.GetDisplayWidth(_sandbox); // " · " + lock + space + sandbox
        }

        if (_lspServerCount > 0 && level < 4)
        {
            width += 3 + 5 + DigitLength(_lspServerCount) + 1; // " · LSP: Ns"
            if (level < 3)
            {
                if (_lspWarningCount > 0)
                    width += 3 + 2 + DigitLength(_lspWarningCount);
                if (_lspErrorCount > 0)
                    width += 3 + 2 + DigitLength(_lspErrorCount);
            }
        }

        if (_mcpSummary is { HasActivity: true } mcp && level < 4)
        {
            width += 3; // " · "
            if (mcp.Connecting > 0)
            {
                width += mcp.Expected > 0 ? 15 : 9;
            }
            else if (mcp.Connected > 0)
            {
                width += 5 + DigitLength(mcp.Connected) + 1;
                if (mcp.ToolCount > 0 && level < 2)
                    width += 3 + DigitLength(mcp.ToolCount) + 1;
            }

            if (mcp.Failed > 0)
            {
                width += 3 + 4 + DigitLength(mcp.Failed);
            }
        }

        return width;
    }

    private static int DigitLength(int n)
    {
        if (n <= 9) return 1;
        if (n <= 99) return 2;
        if (n <= 999) return 3;
        return n.ToString(CultureInfo.InvariantCulture).Length;
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

    private (string ModeTag, string StrategyLabel, string TeamLabel, int RightWidth, int RightCol) MeasureModeTag(int width)
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
        return (modeTag, strategyLabel, teamLabel, rightWidth, rightCol);
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
}
