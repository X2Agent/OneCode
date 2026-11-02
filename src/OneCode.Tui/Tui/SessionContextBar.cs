namespace OneCode.Tui;

using OneCode.Core.Commands;

/// <summary>
/// Session context bar — 1 row at the very bottom.
///
///   📁 project  🌿 main        轮次 5 · 12.5K↓ 8.3K↑ · ctx 200K [██████░░░░] 45%
///   ^^^^^^^^^^^^^^^^^^^^^^^^   ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
///   LEFT: workspace identity    RIGHT: consumption metrics
///
/// Left: workspace name, git branch/worktree (where am I).
/// Right: turn number, token usage, context-window visual bar (how much used).
/// 会话名不在此显示——辨认会话是恢复选择器（/session）的职责。
/// </summary>
public sealed class SessionContextBar : View
{
    private string _workspace = string.Empty;
    private string? _branch;
    private string? _worktree;
    private bool _gitAvailable;

    private int _turnNumber;
    private int _inputTokens;
    private int _outputTokens;
    private int _maxContextTokens;
    private int _currentContextTokens;
    private WorkingMode _mode = WorkingMode.Build;

    public SessionContextBar()
    {
        CanFocus = false;
        Width = Dim.Fill();
        Height = 1;
    }

    public void SetTurn(int turn) { _turnNumber = turn; SetNeedsDraw(); }
    public void SetTokens(int input, int output) { _inputTokens = input; _outputTokens = output; SetNeedsDraw(); }
    public void SetContextUsage(int maxTokens, int currentTokens)
    {
        _maxContextTokens = maxTokens;
        _currentContextTokens = currentTokens;
        SetNeedsDraw();
    }

    /// <summary>
    /// 同步当前工作模式（由 <see cref="ReplShell"/> 订阅 ModeChanged 驱动）。
    /// GOAL 模式下隐藏右侧 ctx 上下文进度条——该模式的上下文消耗由
    /// 各自编排层管理，单轮百分比快照对用户没有参考意义，展示反而造成噪音。
    /// </summary>
    public void SetWorkingMode(WorkingMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        SetNeedsDraw();
    }

    /// <summary>GOAL 模式不显示上下文占用进度条（internal 便于单测）。</summary>
    internal static bool ShowsContextGauge(WorkingMode mode)
        => mode is not WorkingMode.Goal;

    /// <summary>
    /// Refresh git context. Called once on startup (or on directory/worktree change).
    /// Silently no-ops if git is unavailable.
    /// </summary>
    public async Task RefreshAsync(
        IGitHelper? gitHelper = null,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        var dir = workingDirectory ?? Environment.CurrentDirectory;
        _workspace = ShortenPath(dir);

        try
        {
            if (gitHelper is null)
            {
                _gitAvailable = false;
            }
            else
            {
                var inside = await gitHelper.ReadAsync(
                    ["rev-parse", "--is-inside-work-tree"], dir, ct).ConfigureAwait(false);
                _gitAvailable = string.Equals(inside.Trim(), "true", StringComparison.Ordinal);
                if (_gitAvailable)
                {
                    var branch = await gitHelper.ReadAsync(
                        ["rev-parse", "--abbrev-ref", "HEAD"], dir, ct).ConfigureAwait(false);
                    _branch = string.IsNullOrWhiteSpace(branch) || branch.StartsWith('(')
                        ? null
                        : branch.Trim();

                    var porcelain = await gitHelper.ReadAsync(
                        ["worktree", "list", "--porcelain"], dir, ct).ConfigureAwait(false);
                    _worktree = ParseWorktreeName(
                        porcelain.StartsWith('(') ? null : porcelain, dir);
                }
            }
        }
        catch
        {
            _gitAvailable = false;
        }

        SetNeedsDraw();
    }

    private static string? ParseWorktreeName(string? porcelainOutput, string currentDir)
    {
        if (string.IsNullOrEmpty(porcelainOutput)) return null;

        var normalizedCurrent = System.IO.Path.GetFullPath(currentDir).TrimEnd('\\', '/');

        foreach (var line in porcelainOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("worktree ", StringComparison.Ordinal)) continue;
            var path = line["worktree ".Length..].Trim();
            var normalized = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');
            if (string.Equals(normalized, normalizedCurrent, StringComparison.OrdinalIgnoreCase))
            {
                var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
                return string.IsNullOrEmpty(name) ? null : name;
            }
        }
        return null;
    }

    internal static string ShortenPath(string path, int maxChars = 20)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        // 手工按两种分隔符取末段：Path.GetFileName 在 Unix 上只认 '/',
        // 会把 "C:\projects\OneCode" 整体当作文件名（跨平台语义以测试锚定）。
        var trimmed = path.TrimEnd('\\', '/');
        var name = trimmed[(trimmed.LastIndexOfAny(['\\', '/']) + 1)..];
        if (string.IsNullOrEmpty(name)) name = path;

        if (name.Length <= maxChars) return name;
        if (maxChars <= 3) return name[..maxChars];

        var keepChars = maxChars - 1; // 1 for ellipsis
        var head = keepChars / 2;
        var tail = keepChars - head;
        return name[..head] + TuiGlyphs.Ellipsis + name[^tail..];
    }

    private static string FormatTokens(int tokens)
    {
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000.0:F1}M";
        if (tokens >= 1_000) return $"{tokens / 1_000.0:F1}K";
        return tokens.ToString(CultureInfo.InvariantCulture);
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var w = Viewport.Width;
        if (w <= 0) return false;

        Move(0, 0);
        SetAttribute(new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary));
        AddStr(new string(' ', w));

        var rightSegs = BuildRightSegments(w);
        var rightWidth = 0;
        foreach (var s in rightSegs)
            rightWidth += TextWidthHelper.GetDisplayWidth(s.Text);

        var maxLeftCol = rightWidth > 0 ? Math.Max(1, w - rightWidth - 2) : w - 1;
        var col = 1;

        // 📁 workspace (always shown, shortened if needed)
        var wsWidth = TextWidthHelper.GetDisplayWidth(_workspace);
        var availForWs = maxLeftCol - col - 3; // for emoji + space
        var displayWs = _workspace;
        if (availForWs < wsWidth && availForWs > 3)
        {
            displayWs = ShortenPath(_workspace, availForWs);
        }

        if (col + 3 + TextWidthHelper.GetDisplayWidth(displayWs) <= maxLeftCol || col == 1)
        {
            Move(col, 0);
            SetAttribute(new Attribute(TuiPalette.FgSecondary, TuiPalette.BgPrimary));
            AddStr("\U0001f4c1 ");
            SetAttribute(new Attribute(TuiPalette.FgSecondary, TuiPalette.BgPrimary));
            AddStr(displayWs);
            col += 3 + TextWidthHelper.GetDisplayWidth(displayWs); // emoji 2 列 + 空格 1 列
        }

        // 🌿 branch (if git available)
        if (_gitAvailable && !string.IsNullOrEmpty(_branch))
        {
            var branchWidth = 6 + TextWidthHelper.GetDisplayWidth(_branch); // " · " + emoji + 空格
            if (col + branchWidth <= maxLeftCol)
            {
                Move(col, 0);
                SetAttribute(new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary));
                AddStr(" \u00b7 ");
                col += 3;
                SetAttribute(new Attribute(TuiPalette.FgSecondary, TuiPalette.BgPrimary));
                AddStr("\U0001f33f ");
                col += 3; // emoji 2 列 + 空格 1 列
                SetAttribute(new Attribute(TuiPalette.FgSecondary, TuiPalette.BgPrimary));
                AddStr(_branch);
                col += TextWidthHelper.GetDisplayWidth(_branch);
            }
            else if (maxLeftCol - col >= 10)
            {
                var availBranch = maxLeftCol - col - 6;
                var shortBranch = TextWidthHelper.TruncateByWidth(_branch, availBranch);
                Move(col, 0);
                SetAttribute(new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary));
                AddStr(" \u00b7 ");
                col += 3;
                SetAttribute(new Attribute(TuiPalette.FgSecondary, TuiPalette.BgPrimary));
                AddStr("\U0001f33f ");
                col += 3; // emoji 2 列 + 空格 1 列
                SetAttribute(new Attribute(TuiPalette.FgSecondary, TuiPalette.BgPrimary));
                AddStr(shortBranch);
                col += TextWidthHelper.GetDisplayWidth(shortBranch);
            }
        }

        // 📦 worktree (only if inside a linked worktree AND terminal is wide enough)
        if (_gitAvailable && !string.IsNullOrEmpty(_worktree) && w >= 90)
        {
            var wtWidth = 6 + TextWidthHelper.GetDisplayWidth(_worktree); // " · " + emoji + 空格
            if (col + wtWidth <= maxLeftCol)
            {
                Move(col, 0);
                SetAttribute(new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary));
                AddStr(" \u00b7 ");
                col += 3;
                SetAttribute(new Attribute(TuiPalette.FgSecondary, TuiPalette.BgPrimary));
                AddStr("\U0001f4e6 ");
                col += 3; // emoji 2 列 + 空格 1 列
                SetAttribute(new Attribute(TuiPalette.FgSecondary, TuiPalette.BgPrimary));
                AddStr(_worktree);
                col += TextWidthHelper.GetDisplayWidth(_worktree);
            }
        }

        if (rightWidth > 0)
        {
            var rightCol = Math.Max(col + 1, w - rightWidth - 1);
            Move(rightCol, 0);
            foreach (var (text, color) in rightSegs)
            {
                SetAttribute(new Attribute(color, TuiPalette.BgPrimary));
                AddStr(text);
            }
        }

        return true;
    }

    /// <summary>
    /// 构建右侧消耗指标的分段列表（轮次 · token · 上下文进度条）。
    /// 各段之间用 " · " 分隔，整体右对齐渲染。
    /// </summary>
    /// <remarks>
    /// 按<b>优先级</b>组装：上下文进度条 &gt; token 用量 &gt; 轮次。装不下时从最低优先级
    /// 整组丢弃，而不是让 Terminal.Gui 裁掉右侧——否则进度条会顶出视口，
    /// 用户看到的正是「条满屏、百分比不见」。
    /// </remarks>
    private List<(string Text, Color Color)> BuildRightSegments(int viewportWidth)
    {
        List<List<(string Text, Color Color)>> groups = [];

        if (_turnNumber > 0)
            groups.Add([($"轮次 {_turnNumber}", TuiPalette.FgSecondary)]);

        if (_inputTokens > 0 || _outputTokens > 0)
        {
            var tokenStr = $"{FormatTokens(_inputTokens)}{TuiGlyphs.ArrowDown} {FormatTokens(_outputTokens)}{TuiGlyphs.ArrowUp}";
            groups.Add([(tokenStr, TuiPalette.FgSecondary)]);
        }

        // GOAL 模式不显示占用条：每轮由编排层自管，单轮快照没有参考价值。
        if (_maxContextTokens > 0 && ShowsContextGauge(_mode))
            groups.Add(BuildContextGaugeSegments());

        // 从最低优先级（列表头部）开始整组丢弃，直到总宽装得下。
        var budget = Math.Max(0, viewportWidth - 2);
        while (groups.Count > 0 && MeasureRightWidth(groups) > budget)
            groups.RemoveAt(0);

        List<(string Text, Color Color)> segs = [];
        foreach (var group in groups)
        {
            if (segs.Count > 0) segs.Add((" · ", TuiPalette.FgMuted));
            segs.AddRange(group);
        }
        return segs;
    }

    internal List<(string Text, Color Color)> BuildRightSegmentsForTest(int viewportWidth)
        => BuildRightSegments(viewportWidth);

    /// <summary>当前上下文占用快照（测试接缝）。</summary>
    internal int CurrentContextTokensForTest => _currentContextTokens;

    /// <summary>run 累计输入快照（测试接缝）。</summary>
    internal int TotalInputTokensForTest => _inputTokens;

    private static int MeasureRightWidth(List<List<(string Text, Color Color)>> groups)
    {
        var width = (groups.Count - 1) * 3; // " · " 分隔符
        foreach (var group in groups)
            foreach (var (text, _) in group)
                width += TextWidthHelper.GetDisplayWidth(text);
        return width;
    }

    private List<(string Text, Color Color)> BuildContextGaugeSegments()
    {
        const int barWidth = 10;
        var ratio = _maxContextTokens <= 0 ? 0 : (double)_currentContextTokens / _maxContextTokens;
        var pct = Math.Min(100, (int)Math.Round(ratio * 100, MidpointRounding.AwayFromZero));
        var pctColor = pct >= 80 ? TuiPalette.Error
            : pct >= 50 ? TuiPalette.Warning
            : TuiPalette.FgSecondary;

        // 占用可能超过窗口（历史口径下累计输入必然越界），条形必须钳制在 barWidth 内。
        var filledWidth = Math.Clamp((int)Math.Round(ratio * barWidth, MidpointRounding.AwayFromZero), 0, barWidth);
        var emptyWidth = barWidth - filledWidth;

        List<(string Text, Color Color)> segs = [
            ("ctx ", TuiPalette.FgMuted),
            ($"{FormatTokens(_maxContextTokens)} ", TuiPalette.FgSecondary),
            ("[", TuiPalette.FgMuted),
        ];
        if (filledWidth > 0)
            segs.Add((new string(TuiGlyphs.BlockFull[0], filledWidth), pctColor));
        if (emptyWidth > 0)
            segs.Add((new string(TuiGlyphs.BlockLight[0], emptyWidth), TuiPalette.FgMuted));
        segs.Add(("] ", TuiPalette.FgMuted));
        segs.Add(($"{pct,3}%", pctColor));
        return segs;
    }
}
