using OneCode.App.Services.Lsp;


using OneCode.Core.Lsp;

namespace OneCode.App.Tui;

/// <summary>
/// Chat content renderers. All return FormattedLine sequences for ChatTranscriptView.
/// Structured cards (BuildRun / Scope / Delivery / Plan) live in
/// <c>ChatBlockRenderers.Cards.cs</c>.
/// </summary>
public static partial class ChatBlockRenderers
{
    private static readonly string[] CircledNumbers = { "\u2460", "\u2461", "\u2462", "\u2463", "\u2464", "\u2465", "\u2466", "\u2467", "\u2468", "\u2469" };

    public static IReadOnlyList<FormattedLine> RenderModeBanner(WorkingMode mode, int maxWidth = 0)
    {
        var (tag, desc, fg) = mode switch
        {
            WorkingMode.Build => ("BUILD", "直接执行，适合小改动和探索性任务", TuiPalette.ModeBuildFg),
            WorkingMode.Plan => ("PLAN", "先出计划再执行，适合复杂重构", TuiPalette.ModePlanFg),
            // TEAM：编排模式由所选团队的 team.yaml 固定声明，横幅不再区分策略。
            WorkingMode.Team => ("TEAM", "多 Agent 协作——模式由团队 team.yaml 固定声明", TuiPalette.ModeTeamFg),
            WorkingMode.Goal => ("GOAL", "自主分解目标并迭代验证", TuiPalette.ModeGoalFg),
            _ => ("BUILD", "直接执行，适合小改动和探索性任务", TuiPalette.ModeBuildFg),
        };
        // 窄终端防溢出：maxWidth > 0 时按显示宽度截断（CJK 感知），默认不截断。
        var text = $" {tag}  {desc}";
        if (maxWidth > 0)
        {
            var budget = Math.Max(12, maxWidth - 1);
            if (TextWidthHelper.GetDisplayWidth(text) > budget)
                text = TextWidthHelper.TruncateByWidth(text, budget);
        }
        return new[]
        {
            FormattedLine.Plain("", TuiPalette.BgPrimary),
            FormattedLine.FromSegments(new[]
            {
                new LineSegment(TuiGlyphs.BarQuote, fg),
                new LineSegment(text, TuiPalette.FgMuted),
            }),
        };
    }

    public static IReadOnlyList<FormattedLine> RenderDiffBlock(string fileName,
        IReadOnlyList<string> addedLines, IReadOnlyList<string> removedLines,
        int? addedSummary = null, int? removedSummary = null, string? agentName = null,
        int viewWidth = 80)
    {
        var list = new List<FormattedLine>();
        var hdr = $"   \U0001f4c4 {fileName}";
        if (addedSummary is { } a) hdr += $"  +{a}";
        if (removedSummary is { } r) hdr += $"  -{r}";

        // 对话视图渲染时按 contentWidth 截断不换行；文件头与 diff 行（代码行可能很长）
        // 必须在此按 viewWidth 预换行，否则超宽内容不可见。
        foreach (var line in TextWidthHelper.WordWrapByWidth(hdr, Math.Max(8, viewWidth)))
            list.Add(FormattedLine.Plain(line, TuiPalette.Accent));

        // TEAM 归属标注：diff 头后追加执行者（角色专属色），多成员并发修改时可追溯。
        if (!string.IsNullOrWhiteSpace(agentName))
            foreach (var line in TextWidthHelper.WordWrapByWidth(
                $"   · by {agentName}", Math.Max(8, viewWidth)))
                list.Add(FormattedLine.Plain(line, TuiPalette.FromAgentName(agentName)));

        // 换行预算按续行前缀（6 列）扣除——首行前缀只有 3 列，若按首行预算
        // （viewWidth - 4）折行，续行（前缀 6 列）会超出视口 2 列被绘制层裁掉。
        var diffWidth = Math.Max(8, viewWidth - 6);
        foreach (var l in addedLines) AddDiffLines(list, "+" + l, TuiPalette.DiffAdded, diffWidth);
        foreach (var l in removedLines) AddDiffLines(list, "-" + l, TuiPalette.DiffRemoved, diffWidth);
        return list;
    }

    private static void AddDiffLines(List<FormattedLine> list, string text, Color color, int width)
    {
        var wrapped = TextWidthHelper.WordWrapByWidth(text, width);
        if (wrapped.Count == 0)
            return;
        list.Add(FormattedLine.Plain($"   {wrapped[0]}", color));
        foreach (var continuation in wrapped.Skip(1))
            list.Add(FormattedLine.Plain($"      {continuation}", color));
    }

    public static IReadOnlyList<FormattedLine> RenderModeProgress(TuiModeProgress progress, int viewWidth = 80)
    {
        var (glyph, color) = progress.State switch
        {
            ModeProgressState.Completed => (TuiGlyphs.Complete, TuiPalette.Success),
            ModeProgressState.Failed => (TuiGlyphs.Failed, TuiPalette.Error),
            ModeProgressState.Paused or ModeProgressState.Waiting => (TuiGlyphs.Paused, TuiPalette.Warning),
            _ => (TuiGlyphs.InProgress, progress.Mode switch
            {
                WorkingMode.Plan => TuiPalette.ModePlanFg,
                WorkingMode.Team => TuiPalette.ModeTeamFg,
                WorkingMode.Goal => TuiPalette.ModeGoalFg,
                _ => TuiPalette.Accent,
            }),
        };
        var progressText = progress.Completed is { } completed && progress.Total is { } total && total > 0
            ? $"（{completed}/{total}）"
            : string.Empty;
        var text = $"  {glyph} {progress.Message}{progressText}";
        return FitToWidth([FormattedLine.Plain(text, color)], viewWidth);
    }

    public static IReadOnlyList<FormattedLine> RenderAgentMessage(string agentName, string? agentColor, string content,
        DateTimeOffset? timestamp = null, int viewWidth = 80)
    {
        var c = TuiPalette.FromAgentName(agentName);

        var lines = new List<FormattedLine> { FormattedLine.Plain("", TuiPalette.BgPrimary) };

        // Compact agent identifier line: ▸ AgentName (首字母大写，统一角色名显示)
        var displayName = string.IsNullOrEmpty(agentName)
            ? agentName
            : char.ToUpperInvariant(agentName[0]) + agentName[1..];
        lines.Add(FormattedLine.FromSegments(new[]
        {
            new LineSegment("  ", TuiPalette.BgPrimary),
            new LineSegment($"{TuiGlyphs.ToolCall} ", c),
            new LineSegment(displayName, c),
        }));

        // Wrap agent content to fit within viewWidth, accounting for the 4-space indent.
        // Without this, long lines (common in TEAM mode) overflow the terminal width.
        var contentMaxWidth = Math.Max(10, viewWidth - 4);
        var wrappedLines = TextWidthHelper.WordWrapByWidth(content, contentMaxWidth);

        foreach (var line in wrappedLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                lines.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
                continue;
            }
            lines.Add(FormattedLine.Plain($"    {line}", TuiPalette.FgPrimary));
        }

        return lines;
    }

    /// <summary>
    /// Renders an inline LSP diagnostics block for a file. Shows a header line
    /// with the file name and error/warning counts, followed by one line per
    /// diagnostic (sorted by severity, then by line number).
    /// Uses TuiPalette colors throughout — no hardcoded color values.
    /// <paramref name="viewWidth"/> &gt; 0 时消息按显示宽度截断、整块经
    /// <see cref="FitToWidth"/> 兜底（旧实现按字符数截断，CJK 双宽字符会溢出）。
    /// </summary>
    public static IReadOnlyList<FormattedLine> RenderLspDiagnosticsBlock(
        string fileName, IReadOnlyList<LspDiagnostic> diagnostics, int viewWidth = 0)
    {
        var list = new List<FormattedLine> { FormattedLine.Plain("", TuiPalette.BgPrimary) };

        var errors = diagnostics.Count(d => d.Severity == LspDiagnosticSeverity.Error);
        var warnings = diagnostics.Count(d => d.Severity == LspDiagnosticSeverity.Warning);
        var headerColor = errors > 0 ? TuiPalette.Error : TuiPalette.Warning;

        list.Add(FormattedLine.FromSegments(new[]
        {
            new LineSegment($"  {TuiGlyphs.BorderVertical} ", headerColor),
            new LineSegment($"LSP Diagnostics — {fileName}", headerColor),
            new LineSegment($"  \u00b7 {errors} error(s) \u00b7 {warnings} warning(s)", TuiPalette.FgMuted),
        }));

        var ordered = diagnostics
            .OrderBy(d => d.Severity)
            .ThenBy(d => d.Range.StartLine)
            .ThenBy(d => d.Range.StartColumn)
            .ToList();

        foreach (var d in ordered)
        {
            var (prefix, color) = d.Severity switch
            {
                LspDiagnosticSeverity.Error => ("[E]", TuiPalette.Error),
                LspDiagnosticSeverity.Warning => ("[W]", TuiPalette.Warning),
                LspDiagnosticSeverity.Information => ("[I]", TuiPalette.Info),
                LspDiagnosticSeverity.Hint => ("[H]", TuiPalette.FgSecondary),
                _ => ("[?]", TuiPalette.FgMuted),
            };

            var line = d.Range.StartLine + 1; // LSP uses 0-based lines; display 1-based
            var col = d.Range.StartColumn + 1;
            var message = d.Message;
            // 按显示宽度截断消息（预留滚动条列），而非旧的字符数截断。
            if (viewWidth > 0)
            {
                var rowPrefixWidth = TextWidthHelper.GetDisplayWidth($"    {prefix} L{line}:C{col} ");
                var budget = Math.Max(8, viewWidth - rowPrefixWidth - 1);
                if (TextWidthHelper.GetDisplayWidth(d.Message) > budget)
                    message = TextWidthHelper.TruncateByWidth(d.Message, budget);
            }
            else if (message.Length > 80)
            {
                message = message[..80] + TuiGlyphs.Ellipsis;
            }

            list.Add(FormattedLine.FromSegments(new[]
            {
                new LineSegment($"    {prefix} ", color),
                new LineSegment($"L{line}:C{col} ", TuiPalette.FgMuted),
                new LineSegment(message, TuiPalette.FgPrimary),
            }));
        }

        // 兜底：头部行（fileName 可能很长）与任何残留超宽行整体截断。
        return viewWidth > 0 ? FitToWidth(list, viewWidth) : list;
    }
}
