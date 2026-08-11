using OneCode.App.Services.Lsp;
using OneCode.Core.Build;
using OneCode.Core.Lsp;

namespace OneCode.App.Tui;

/// <summary>Chat content renderers. All return FormattedLine sequences for ChatTranscriptView.</summary>
public static class ChatBlockRenderers
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

    public static IReadOnlyList<FormattedLine> RenderBuildRunPanel(
        TuiBuildRunState state,
        int viewWidth = 80)
    {
        var (glyph, message, color) = state.State switch
        {
            BuildRunState.Created or BuildRunState.Intake or BuildRunState.Assessing
                => (TuiGlyphs.InProgress, "正在分析任务…", TuiPalette.ModeBuildFg),
            BuildRunState.Clarifying
                => (TuiGlyphs.Paused, "等待补充任务信息…", TuiPalette.Warning),
            BuildRunState.ScopeConfirmed or BuildRunState.Planning or BuildRunState.Planned
                => (TuiGlyphs.InProgress, "正在准备执行…", TuiPalette.ModeBuildFg),
            BuildRunState.Implementing when state.TotalTasks > 0
                => (TuiGlyphs.InProgress, $"正在执行任务（{state.CompletedTasks}/{state.TotalTasks}）…", TuiPalette.ModeBuildFg),
            BuildRunState.Implementing when state.ChangedFiles > 0
                => (TuiGlyphs.InProgress, $"正在修改 {state.ChangedFiles} 个文件…", TuiPalette.ModeBuildFg),
            BuildRunState.Implementing
                => (TuiGlyphs.InProgress, "正在执行任务…", TuiPalette.ModeBuildFg),
            BuildRunState.Verifying
                => (TuiGlyphs.InProgress, "正在运行验证…", TuiPalette.Accent),
            BuildRunState.Accepting
                => (TuiGlyphs.InProgress, "验证通过，正在完成提交…", TuiPalette.Accent),
            BuildRunState.Recovering
                => (TuiGlyphs.InProgress, "正在恢复任务…", TuiPalette.Warning),
            BuildRunState.Completed
                => (TuiGlyphs.Complete, BuildCompletionMessage(state), TuiPalette.Success),
            BuildRunState.Cancelled
                => (TuiGlyphs.Paused, "任务已取消", TuiPalette.FgMuted),
            BuildRunState.Blocked
                => (TuiGlyphs.Failed, "任务被阻塞", TuiPalette.Warning),
            BuildRunState.LimitReached
                => (TuiGlyphs.Failed, "任务因达到轮次上限而停止", TuiPalette.Warning),
            BuildRunState.BudgetExceeded
                => (TuiGlyphs.Failed, "任务因达到预算上限而停止", TuiPalette.Warning),
            _ => (TuiGlyphs.Failed, "任务执行失败", TuiPalette.Error),
        };

        var lines = new List<FormattedLine>
        {
            FormattedLine.FromSegments([
                new LineSegment($"  {glyph} ", color),
                new LineSegment(message, color),
            ]),
        };
        if (state.State is BuildRunState.Failed or BuildRunState.Blocked
            && !string.IsNullOrWhiteSpace(state.FailureSummary))
        {
            AddWrappedDetail(lines, state.FailureSummary, viewWidth, color);
        }

        return FitToWidth(lines, viewWidth);
    }

    private static string BuildCompletionMessage(TuiBuildRunState state)
    {
        var details = new List<string>();
        if (state.ChangedFiles > 0)
            details.Add($"修改 {state.ChangedFiles} 个文件");
        if (state.ValidationStatus == BuildValidationStatus.Passed)
            details.Add("验证通过");
        return details.Count == 0
            ? "任务已完成"
            : $"已完成：{string.Join("，", details)}";
    }

    public static IReadOnlyList<FormattedLine> RenderBuildScopeCard(
        BuildScopeSnapshot scope,
        int viewWidth = 80)
    {
        var requiredAcceptance = scope.AcceptanceCriteria.Count(item => item.Required);
        var lines = new List<FormattedLine>
        {
            FormattedLine.WithBackground(
                "  SCOPE CONFIRMATION  ·  CONFIRMED",
                TuiPalette.Success,
                TuiPalette.BgSuccess),
        };
        AddWrappedField(lines, "Goal", scope.Goal, viewWidth, TuiPalette.FgPrimary);
        lines.Add(FormattedLine.Plain(
            $"  In scope    {scope.InScope.Count} item(s)",
            TuiPalette.FgPrimary));
        foreach (var item in scope.InScope)
            AddWrappedDetail(lines, item, viewWidth, TuiPalette.FgPrimary);
        lines.Add(FormattedLine.Plain(
            $"  Out scope   {scope.OutOfScope.Count} item(s)",
            TuiPalette.FgSecondary));
        lines.Add(FormattedLine.Plain(
            $"  Acceptance  {requiredAcceptance} required / {scope.AcceptanceCriteria.Count} total",
            TuiPalette.FgPrimary));
        lines.Add(FormattedLine.Plain(
            $"  Confirmed   {scope.ConfirmedBy} · {scope.ConfirmedAt:O}",
            TuiPalette.FgSecondary));
        return FitToWidth(lines, viewWidth);
    }

    public static IReadOnlyList<FormattedLine> RenderBuildDeliveryCard(
        BuildRunResult result,
        int viewWidth = 80)
    {
        var validation = result.Validations.LastOrDefault()?.Status.ToString() ?? "not available";
        var completedTasks = result.Tasks.Count(task => task.Status == BuildTaskStatus.Completed);
        var requiredAcceptance = result.Acceptance.Where(item => item.Required).ToArray();
        var passedAcceptance = requiredAcceptance.Count(item => item.Status == AcceptanceStatus.Passed);
        var incompleteTasks = result.Tasks.Count(task => task.Status is not (BuildTaskStatus.Completed or BuildTaskStatus.Skipped));
        var commitLabel = result.TransactionCommitted ? "committed" : result.TransactionRolledBack ? "rolled back" : "not committed";
        var lines = new List<FormattedLine>
        {
            FormattedLine.Plain("", TuiPalette.BgPrimary),
            FormattedLine.WithBackground(
                $"  BUILD DELIVERY  ·  {result.State.ToString().ToUpperInvariant()}",
                result.State == BuildRunState.Completed ? TuiPalette.Success : TuiPalette.Warning,
                result.State == BuildRunState.Completed ? TuiPalette.BgSuccess : TuiPalette.BgTerminalHeader),
            FormattedLine.Plain($"  Run         {result.RunId}", TuiPalette.FgSecondary),
            FormattedLine.Plain($"  Files       {result.ChangedFiles.Count}", TuiPalette.FgPrimary),
            FormattedLine.Plain($"  Tasks       {completedTasks}/{result.Tasks.Count}", TuiPalette.FgPrimary),
            FormattedLine.Plain($"  Validation  {validation}", TuiPalette.FgPrimary),
            FormattedLine.Plain($"  Acceptance  {passedAcceptance}/{requiredAcceptance.Length}", TuiPalette.FgPrimary),
            FormattedLine.Plain($"  Incomplete  {incompleteTasks}", incompleteTasks == 0 ? TuiPalette.Success : TuiPalette.Warning),
            FormattedLine.Plain($"  Transaction {commitLabel}", result.TransactionCommitted ? TuiPalette.Success : TuiPalette.Warning),
        };
        if (!string.IsNullOrWhiteSpace(result.Summary))
            AddWrappedField(lines, "Summary", result.Summary, viewWidth, TuiPalette.FgPrimary);
        if (result.KnownRisks.Count > 0)
        {
            lines.Add(FormattedLine.Plain($"  Known risks {result.KnownRisks.Count}", TuiPalette.Warning));
            foreach (var risk in result.KnownRisks)
                AddWrappedDetail(lines, risk, viewWidth, TuiPalette.FgPrimary);
        }
        return FitToWidth(lines, viewWidth);
    }

    private static void AddWrappedField(
        ICollection<FormattedLine> lines,
        string label,
        string value,
        int viewWidth,
        Color color)
    {
        var prefix = $"  {label,-11}";
        var available = Math.Max(8, viewWidth - TextWidthHelper.GetDisplayWidth(prefix));
        var wrapped = TextWidthHelper.WordWrapByWidth(value, available);
        if (wrapped.Count == 0)
        {
            lines.Add(FormattedLine.Plain(prefix, color));
            return;
        }

        lines.Add(FormattedLine.Plain(prefix + wrapped[0], color));
        var continuation = new string(' ', prefix.Length);
        foreach (var line in wrapped.Skip(1))
            lines.Add(FormattedLine.Plain(continuation + line, color));
    }

    private static void AddWrappedDetail(
        ICollection<FormattedLine> lines,
        string value,
        int viewWidth,
        Color color)
    {
        const string prefix = "    - ";
        var available = Math.Max(8, viewWidth - TextWidthHelper.GetDisplayWidth(prefix));
        var wrapped = TextWidthHelper.WordWrapByWidth(value, available);
        if (wrapped.Count == 0)
            return;

        lines.Add(FormattedLine.Plain(prefix + wrapped[0], color));
        foreach (var line in wrapped.Skip(1))
            lines.Add(FormattedLine.Plain("      " + line, color));
    }

    private static IReadOnlyList<FormattedLine> FitToWidth(
        IEnumerable<FormattedLine> lines,
        int viewWidth)
    {
        var width = Math.Max(20, viewWidth);
        return lines.Select(line =>
        {
            if (TextWidthHelper.GetDisplayWidth(line.FullText) <= width)
                return line;

            var text = TextWidthHelper.TruncateByWidth(line.FullText, width);
            return line.Bg is { } background
                ? FormattedLine.WithBackground(text, line.Color, background)
                : FormattedLine.Plain(text, line.Color);
        }).ToArray();
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

    /// <param name="showActionButtons">保留参数，当前始终为 false。</param>
    public static IReadOnlyList<FormattedLine> RenderPlanCard(
        string title,
        IReadOnlyList<PlanStep> steps,
        int viewWidth = 80,
        bool showActionButtons = false,
        bool showApprovalGuidance = false,
        string? markdown = null)
    {
        var list = new List<FormattedLine>
        {
            FormattedLine.Plain("", TuiPalette.BgPrimary),
            FormattedLine.WithBackground($"  \U0001f4cb  {title}", TuiPalette.ModePlanFg, TuiPalette.BgTerminalHeader),
            FormattedLine.Plain("", TuiPalette.BgPrimary),
        };
        if (showApprovalGuidance && !string.IsNullOrWhiteSpace(markdown))
        {
            list.Add(FormattedLine.Plain("  完整计划", TuiPalette.ModePlanFg));
            foreach (var line in MarkdownRenderer.Render(markdown, Math.Max(20, viewWidth - 2)))
            {
                var color = line.Role switch
                {
                    LineRole.Error => TuiPalette.Error,
                    LineRole.DiffAdded => TuiPalette.DiffAdded,
                    LineRole.DiffRemoved => TuiPalette.DiffRemoved,
                    _ => TuiPalette.FgSecondary,
                };
                list.Add(FormattedLine.Plain("  " + line.Text, color));
            }
            list.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
            list.Add(FormattedLine.Plain("  执行步骤", TuiPalette.ModePlanFg));
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var num = i < CircledNumbers.Length ? CircledNumbers[i] : $"({i + 1})";
            var lc = s.Status switch
            {
                PlanStepStatus.Done => TuiPalette.FgMuted,
                PlanStepStatus.Current => TuiPalette.Accent,
                _ => TuiPalette.FgPrimary,
            };
            if (string.IsNullOrWhiteSpace(s.Assignee))
            {
                list.Add(FormattedLine.Plain($"  {num} {s.Label}", lc));
            }
            else
            {
                var assigneeTag = $"{TuiGlyphs.ArrowRight} {s.Assignee}";
                var leftPart = $"  {num} {s.Label}";
                var padLen = Math.Max(1, viewWidth - leftPart.Length - assigneeTag.Length - 2);
                list.Add(FormattedLine.FromSegments(new[]
                {
                    new LineSegment(leftPart, lc),
                    new LineSegment(new string(' ', padLen), TuiPalette.BgPrimary),
                    new LineSegment(assigneeTag, TuiPalette.FgMuted),
                }));
            }
            if (!string.IsNullOrWhiteSpace(s.Content))
            {
                const string contentPrefix = "     ";
                var available = Math.Max(20, viewWidth - TextWidthHelper.GetDisplayWidth(contentPrefix));
                foreach (var line in TextWidthHelper.WordWrapByWidth(s.Content, available))
                    list.Add(FormattedLine.Plain(contentPrefix + line, TuiPalette.FgMuted));
            }
        }
        if (showApprovalGuidance)
        {
            list.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
            AddWrappedDetail(
                list,
                "在下方选择“批准并执行”即可切换到 Build 模式并开始执行。",
                viewWidth,
                TuiPalette.Success);
            AddWrappedDetail(
                list,
                "如需调整，选择“输入修改意见”，然后在输入框中直接说明修改要求。",
                viewWidth,
                TuiPalette.FgSecondary);
        }
        if (showActionButtons)
        {
            list.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
            list.Add(FormattedLine.FromSegments(new[]
            {
                new LineSegment("  ", TuiPalette.BgPrimary),
                new LineSegment($" {TuiGlyphs.Complete} 批准 (a) ", TuiPalette.Success, TuiPalette.BgTerminalHeader),
                new LineSegment("  ", TuiPalette.BgPrimary),
                new LineSegment($" {TuiGlyphs.Failed} 拒绝 (r) ", TuiPalette.Error, TuiPalette.BgTerminalHeader),
                new LineSegment("  ", TuiPalette.BgPrimary),
                new LineSegment($" {TuiGlyphs.Ellipsis} 修改步骤 (s) ", TuiPalette.FgSecondary, TuiPalette.BgTerminalHeader),
            }));
        }
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

public enum PlanStepStatus { Pending = 0, Current = 1, Done = 2, }

/// <summary>
/// A single step in a plan card.
/// </summary>
/// <param name="Label">Short label shown as the primary line.</param>
/// <param name="Content">Optional longer description shown as a secondary line under the label.</param>
/// <param name="Assignee">Optional agent name shown as → name on the right.</param>
/// <param name="Status">Pending / Current / Done. Controls color and strikethrough.</param>
public sealed record PlanStep(
    string Label,
    string? Content = null,
    string? Assignee = null,
    PlanStepStatus Status = PlanStepStatus.Pending);
