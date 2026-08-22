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
    private string? _resolvedTeamMode;
    private string _activity = "处理中";
    private string _model = "Opus";
    private string _cost = "$0.00";
    private string _sandbox = "Sandbox";
    private int _lspServerCount;
    private int _lspErrorCount;
    private int _lspWarningCount;

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
    public void SetCost(string c) { _cost = string.IsNullOrWhiteSpace(c) ? "$0.00" : c; SetNeedsDraw(); }
    public string CurrentCost => _cost;
    public void SetSandboxMode(string s) { _sandbox = string.IsNullOrWhiteSpace(s) ? "Sandbox" : s; SetNeedsDraw(); }

    /// <summary>
    /// 更新团队标签。<paramref name="resolvedTeamMode"/> 为 Strategy=Config 时
    /// YAML 实际解析出的模式（如 "Magentic"），用于透出 Config 背后的真实策略（P3-10）。
    /// </summary>
    public void SetActiveTeam(string? teamName, string? resolvedTeamMode = null)
    {
        if (_activeTeam == teamName && _resolvedTeamMode == resolvedTeamMode) return;
        _activeTeam = teamName;
        _resolvedTeamMode = resolvedTeamMode;
        SetNeedsDraw();
    }

    internal static string GetStrategyLabel(TeamStrategy strategy, string? resolvedMode = null) => strategy switch
    {
        TeamStrategy.Config when !string.IsNullOrWhiteSpace(resolvedMode) => $"Config({resolvedMode})",
        TeamStrategy.Config => "Config",
        TeamStrategy.Magentic => "Magentic",
        _ => "GroupChat",
    };

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
        SetAttribute(new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary));
        AddStr(" \u00b7 ");
        SetAttribute(new Attribute(TuiPalette.Warning, TuiPalette.BgPrimary));
        AddStr($"\U0001f4b0 {_cost}");

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

        DrawModeTag(w);
        return true;
    }

    private void DrawModeTag(int width)
    {
        var modeTag = _modeController.ModeTag;
        var strategyLabel = _modeController.ShowStrategyTag
            ? $" \u00b7 {GetStrategyLabel(_modeController.Strategy, _resolvedTeamMode)}"
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
        // Design-spec mode tag: steady state is a colored-background badge with
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
