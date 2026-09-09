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
    private string _sandbox = "Sandbox";
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

        // LEFT: live activity · model · cost · sandbox · LSP.
        var col = 1;
        Move(col, 0);
        if (_busy)
        {
            SetAttribute(new Attribute(TuiPalette.Warning, TuiPalette.BgPrimary));
            AddStr($"{_spinner.CurrentFrame} ");
            SetAttribute(new Attribute(TuiPalette.FgSecondary, TuiPalette.BgPrimary));
            AddStr(_activity);
            SetAttribute(new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary));
            AddStr(" \u00b7 ");
        }

        SetAttribute(new Attribute(TuiPalette.FgPrimary, TuiPalette.BgPrimary));
        AddStr(_model);

        if (_sandbox != "Normal")
        {
            SetAttribute(new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary));
            AddStr(" \u00b7 ");
            SetAttribute(new Attribute(TuiPalette.Info, TuiPalette.BgPrimary));
            AddStr($"\U0001f512 {_sandbox}");
        }

        // LSP status: only rendered when at least one server is running (avoids noise).
        // Format: LSP: 2s · ⚠ 3 · ✗ 1  (servers, warnings, errors)
        if (_lspServerCount > 0)
        {
            SetAttribute(new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary));
            AddStr(" \u00b7 ");
            // Server count — green when no errors, yellow when only warnings, red when errors present
            var serverColor = _lspErrorCount > 0
                ? TuiPalette.Error
                : (_lspWarningCount > 0 ? TuiPalette.Warning : TuiPalette.StatusOk);
            SetAttribute(new Attribute(serverColor, TuiPalette.BgPrimary));
            AddStr($"LSP: {_lspServerCount}s");

            if (_lspWarningCount > 0)
            {
                SetAttribute(new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary));
                AddStr(" \u00b7 ");
                SetAttribute(new Attribute(TuiPalette.Warning, TuiPalette.BgPrimary));
                AddStr($"\u26a0 {_lspWarningCount}");
            }
            if (_lspErrorCount > 0)
            {
                SetAttribute(new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary));
                AddStr(" \u00b7 ");
                SetAttribute(new Attribute(TuiPalette.Error, TuiPalette.BgPrimary));
                AddStr($"\u2717 {_lspErrorCount}");
            }
        }

        // MCP 状态三态（连接中 x/y / 已连 n / 失败 k）。零活动时隐藏降噪；
        // 失败必须始终可见——此前 0 连接被隐藏，坏掉的服务器曾十几秒不可见。
        if (_mcpSummary is { HasActivity: true } mcp)
        {
            SetAttribute(new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary));
            AddStr(" \u00b7 ");

            if (mcp.Connecting > 0)
            {
                // 握手进行中：x = 已就绪数，y = 目标数（按需连接未走 ConnectAll 时 y 未知，仅显示"连接中"）。
                SetAttribute(new Attribute(TuiPalette.Info, TuiPalette.BgPrimary));
                AddStr(mcp.Expected > 0
                    ? $"MCP: 连接中 {mcp.Connected}/{mcp.Expected}"
                    : "MCP: 连接中");
            }
            else if (mcp.Connected > 0)
            {
                SetAttribute(new Attribute(TuiPalette.Info, TuiPalette.BgPrimary));
                AddStr($"MCP: {mcp.Connected}s");
                if (mcp.ToolCount > 0)
                {
                    SetAttribute(new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary));
                    AddStr($" \u00b7 {mcp.ToolCount}t");
                }
            }

            if (mcp.Failed > 0)
            {
                SetAttribute(new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary));
                AddStr(" \u00b7 ");
                SetAttribute(new Attribute(
                    mcp.Connected > 0 ? TuiPalette.Warning : TuiPalette.Error,
                    TuiPalette.BgPrimary));
                if (mcp.Connected > 0 || mcp.Connecting > 0)
                {
                    // 已有 "MCP:" 前缀（连接中/已连分支），失败计数作后缀即可。
                    AddStr($"\u2717{mcp.Failed}失败");
                }
                else
                {
                    // 全部失败（0 连接）：此处是 MCP 状态唯一出口，必须带前缀，
                    // 否则 "✗k失败" 与 LSP 的 ⚠/✗ 计数无法区分（2026-09 反馈）。
                    AddStr($"MCP: \u2717{mcp.Failed}失败");
                }
            }
        }

        DrawModeTag(w);
        return true;
    }

    private void DrawModeTag(int width)
    {
        var modeTag = _modeController.ModeTag;
        // TEAM 模式下显示团队 YAML 声明的编排模式（固定属性，非运行期可变状态）。
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

        Move(rightCol, 0);
        var modeColor = _modeController.Mode switch
        {
            WorkingMode.Build => TuiPalette.ModeBuildFg,
            WorkingMode.Plan => TuiPalette.ModePlanFg,
            WorkingMode.Team => TuiPalette.ModeTeamFg,
            WorkingMode.Goal => TuiPalette.ModeGoalFg,
            _ => TuiPalette.FgSecondary,
        };
        // Mode tag: steady state is a colored-background badge with
        // dark (bg-root) text; the momentary flash inverts to a colored-text
        // highlight so a mode change is still noticed.
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
