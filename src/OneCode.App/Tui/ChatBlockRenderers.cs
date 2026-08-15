using OneCode.App.Services.Lsp;
using OneCode.Core.Lsp;

namespace OneCode.App.Tui;

/// <summary>
/// Chat content renderers. All return FormattedLine sequences for ChatTranscriptView.
/// Structured cards (BuildRun / Scope / Delivery / Plan) live in
/// <see cref="ChatBlockRenderers.Cards.cs"/>.
/// </summary>
public static partial class ChatBlockRenderers
{
    private static readonly string[] CircledNumbers = { "\u2460", "\u2461", "\u2462", "\u2463", "\u2464", "\u2465", "\u2466", "\u2467", "\u2468", "\u2469" };

    public static IReadOnlyList<FormattedLine> RenderModeBanner(WorkingMode mode, TeamStrategy strategy = TeamStrategy.Config)
    {
        var (tag, desc, fg) = mode switch
        {
            WorkingMode.Build => ("BUILD", "直接执行，适合小改动和探索性任务", TuiPalette.ModeBuildFg),
            WorkingMode.Plan => ("PLAN", "先出计划再执行，适合复杂重构", TuiPalette.ModePlanFg),
            WorkingMode.Team when strategy == TeamStrategy.Config
                => ("TEAM · Config", "遵循 team.yaml（YAML）默认编排策略", TuiPalette.ModeTeamFg),
            WorkingMode.Team when strategy == TeamStrategy.GroupChat
                => ("TEAM · GroupChat", "对等轮询讨论，适合头脑风暴", TuiPalette.ModeTeamFg),
            WorkingMode.Goal => ("GOAL", "自主分解目标并迭代验证", TuiPalette.ModeGoalFg),
            _ => ("TEAM · Magentic", "Orchestrator 协调多 Agent 分工", TuiPalette.ModeTeamFg),
        };
        return new[]
        {
            FormattedLine.Plain("", TuiPalette.BgPrimary),
            FormattedLine.FromSegments(new[]
            {
                new LineSegment(TuiGlyphs.BarQuote, fg),
                new LineSegment($" {tag}  {desc}", TuiPalette.FgMuted),
            }),
        };
    }

    public static IReadOnlyList<FormattedLine> RenderToolCallRow(string name, string? args = null, bool ok = true)
    {
        var statusLabel = ok ? "完成" : "错误";
        var statusColor = ok ? TuiPalette.Success : TuiPalette.Error;
        var segments = new List<LineSegment>
        {
            new("  ", TuiPalette.BgPrimary),
            new($"{TuiGlyphs.ToolCall} ", TuiPalette.Accent),
            new(name, TuiPalette.Warning),
        };
        if (!string.IsNullOrWhiteSpace(args))
            segments.Add(new($" \u00b7 {args}", TuiPalette.ToolDetailColor));
        segments.Add(new($" \u00b7 {statusLabel}", statusColor));
        return new[] { FormattedLine.FromSegments(segments.ToArray()) };
    }

    public static IReadOnlyList<FormattedLine> RenderDiffBlock(string fileName,
        IReadOnlyList<string> addedLines, IReadOnlyList<string> removedLines,
        int? addedSummary = null, int? removedSummary = null)
    {
        var list = new List<FormattedLine>();
        var hdr = $"   \U0001f4c4 {fileName}";
        if (addedSummary is { } a) hdr += $"  +{a}";
        if (removedSummary is { } r) hdr += $"  -{r}";
        list.Add(FormattedLine.Plain(hdr, TuiPalette.Accent));
        foreach (var l in addedLines) list.Add(FormattedLine.Plain($"   +{l}", TuiPalette.DiffAdded));
        foreach (var l in removedLines) list.Add(FormattedLine.Plain($"   -{l}", TuiPalette.DiffRemoved));
        return list;
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

    public static IReadOnlyList<FormattedLine> RenderAgentCoordinationMessage(string fromName, string? fromColor, string toName, string? toColor, string? content = null)
    {
        var f = TuiPalette.FromAgentName(fromName);
        var t = TuiPalette.FromAgentName(toName);
        var segments = new List<LineSegment>
        {
            new($" {TuiGlyphs.BorderVertical} ", TuiPalette.FgMuted),
            new(fromName, f),
            new($" {TuiGlyphs.ArrowRight} ", TuiPalette.FgMuted),
            new(toName, t),
        };
        if (!string.IsNullOrWhiteSpace(content))
            segments.Add(new($"  {content}", TuiPalette.FgSecondary));
        return new[]
        {
            FormattedLine.Plain("", TuiPalette.BgPrimary),
            FormattedLine.FromSegments(segments.ToArray()),
        };
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

    public static IReadOnlyList<FormattedLine> RenderThinkingBlock(string? thinking = null)
    {
        return new[]
        {
            FormattedLine.FromSegments(new[]
            {
                new LineSegment("  ", TuiPalette.BgPrimary),
                new LineSegment($"{TuiGlyphs.Collapsed} 思考：{thinking ?? "思考中\u2026"}", TuiPalette.FgMuted),
            })
        };
    }

    /// <summary>
    /// Renders an inline LSP diagnostics block for a file. Shows a header line
    /// with the file name and error/warning counts, followed by one line per
    /// diagnostic (sorted by severity, then by line number).
    /// Uses TuiPalette colors throughout — no hardcoded color values.
    /// </summary>
    public static IReadOnlyList<FormattedLine> RenderLspDiagnosticsBlock(
        string fileName, IReadOnlyList<LspDiagnostic> diagnostics)
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
            var message = d.Message.Length > 80 ? d.Message[..80] + TuiGlyphs.Ellipsis : d.Message;

            list.Add(FormattedLine.FromSegments(new[]
            {
                new LineSegment($"    {prefix} ", color),
                new LineSegment($"L{line}:C{col} ", TuiPalette.FgMuted),
                new LineSegment(message, TuiPalette.FgPrimary),
            }));
        }

        return list;
    }
}
