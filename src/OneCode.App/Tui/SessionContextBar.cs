namespace OneCode.App.Tui;

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
    private string? _sessionName;

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

    /// <summary>设置当前会话名，显示在 workspace 旁边。传 null 隐藏。</summary>
    public void SetSessionName(string? name)
    {
        _sessionName = name;
        SetNeedsDraw();
    }

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

    private static string ShortenPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        if (!string.IsNullOrEmpty(name)) return name;
        return path;
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

        // Clear line
        Move(0, 0);
        SetAttribute(new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary));
        AddStr(new string(' ', w));

        var col = 1;
        Move(col, 0);

        // 📁 workspace (always shown)
        SetAttribute(new Attribute(TuiPalette.FgSecondary, TuiPalette.BgPrimary));
        AddStr("\U0001f4c1 ");
        SetAttribute(new Attribute(TuiPalette.FgPrimary, TuiPalette.BgPrimary));
        AddStr(_workspace);
        // Emoji 📁 is a surrogate pair (display width 2) + trailing space = 3 cols.
        // Use display width for workspace so CJK / emoji names don't misalign the right side.
        col += 2 + TextWidthHelper.GetDisplayWidth(_workspace);

        // 🌿 branch (if git available)
        if (_gitAvailable && !string.IsNullOrEmpty(_branch))
        {
            col += 2;
            Move(col, 0);
            SetAttribute(new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary));
            AddStr("\u00b7 ");
            col += 2;
            SetAttribute(new Attribute(TuiPalette.Accent, TuiPalette.BgPrimary));
            AddStr("\U0001f33f ");
            col += 2;
            SetAttribute(new Attribute(TuiPalette.FgPrimary, TuiPalette.BgPrimary));
            AddStr(_branch);
            col += TextWidthHelper.GetDisplayWidth(_branch);
        }

        // 📦 worktree (only if inside a linked worktree AND terminal is wide enough)
        if (_gitAvailable && !string.IsNullOrEmpty(_worktree) && w >= 90)
        {
            col += 2;
            Move(col, 0);
            SetAttribute(new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary));
            AddStr("\u00b7 ");
            col += 2;
            SetAttribute(new Attribute(TuiPalette.Info, TuiPalette.BgPrimary));
            AddStr("\U0001f4e6 ");
            col += 2;
            SetAttribute(new Attribute(TuiPalette.FgSecondary, TuiPalette.BgPrimary));
            AddStr(_worktree);
            col += TextWidthHelper.GetDisplayWidth(_worktree);
        }

        // Session name (if set and terminal is wide enough)
        if (!string.IsNullOrEmpty(_sessionName) && w >= 80)
        {
            col += 2;
            Move(col, 0);
            SetAttribute(new Attribute(TuiPalette.FgMuted, TuiPalette.BgPrimary));
            AddStr("\u00b7 ");
            col += 2;
            SetAttribute(new Attribute(TuiPalette.Info, TuiPalette.BgPrimary));
            AddStr("\U0001f4dd ");
            col += 2;
            SetAttribute(new Attribute(TuiPalette.FgSecondary, TuiPalette.BgPrimary));
            AddStr(_sessionName);
            col += TextWidthHelper.GetDisplayWidth(_sessionName);
        }

        var rightSegs = BuildRightSegments(w);
        var rightWidth = 0;
        foreach (var s in rightSegs)
            rightWidth += TextWidthHelper.GetDisplayWidth(s.Text);

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
    private List<(string Text, Color Color)> BuildRightSegments(int viewportWidth)
    {
        var segs = new List<(string Text, Color Color)>();

        // 轮次 (if > 0, hidden on very narrow terminals)
        if (_turnNumber > 0 && viewportWidth >= 60)
        {
            if (segs.Count > 0) segs.Add((" · ", TuiPalette.FgMuted));
            segs.Add(($"轮次 {_turnNumber}", TuiPalette.FgSecondary));
        }

        // Token usage (if any, hidden on narrow terminals)
        if (_inputTokens > 0 || _outputTokens > 0)
        {
            if (viewportWidth >= 70)
            {
                if (segs.Count > 0) segs.Add((" · ", TuiPalette.FgMuted));
                var tokenStr = $"{FormatTokens(_inputTokens)}{TuiGlyphs.ArrowDown} {FormatTokens(_outputTokens)}{TuiGlyphs.ArrowUp}";
                segs.Add((tokenStr, TuiPalette.FgSecondary));
            }
        }

        // Context window usage — visual progress bar with max context (if available)
        // Hidden on narrow terminals (<50) to avoid overflow
        if (_maxContextTokens > 0 && viewportWidth >= 50)
        {
            if (segs.Count > 0) segs.Add((" · ", TuiPalette.FgMuted));

            var ratio = (double)_currentContextTokens / _maxContextTokens;
            var pct = Math.Min(100, (int)Math.Round(ratio * 100, MidpointRounding.AwayFromZero));
            var pctColor = pct >= 80 ? TuiPalette.Error
                : pct >= 50 ? TuiPalette.Warning
                : TuiPalette.StatusOk;

            const int barWidth = 10;
            var filledWidth = (int)Math.Round(ratio * barWidth, MidpointRounding.AwayFromZero);
            var emptyWidth = barWidth - filledWidth;

            segs.Add(("ctx ", TuiPalette.FgMuted));
            segs.Add(($"{FormatTokens(_maxContextTokens)} ", TuiPalette.FgSecondary));
            segs.Add(("[", TuiPalette.FgMuted));
            if (filledWidth > 0)
                segs.Add((new string(TuiGlyphs.BlockFull[0], filledWidth), pctColor));
            if (emptyWidth > 0)
                segs.Add((new string(TuiGlyphs.BlockLight[0], emptyWidth), TuiPalette.FgMuted));
            segs.Add(("] ", TuiPalette.FgMuted));
            segs.Add(($"{pct,3}%", pctColor));
        }

        return segs;
    }
}
