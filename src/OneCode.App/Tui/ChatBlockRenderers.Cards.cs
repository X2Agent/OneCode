using OneCode.Core.Build;

namespace OneCode.App.Tui;

/// <summary>
/// Structured card renderers for <see cref="ChatBlockRenderers"/>:
/// BuildRun 进度面板、Scope/Delivery 卡片与 Plan 卡片，及共享的
/// 换行 / 宽度适配辅助方法。
/// </summary>
public static partial class ChatBlockRenderers
{
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

    public static IReadOnlyList<FormattedLine> RenderPlanCard(
        string title,
        IReadOnlyList<PlanStep> steps,
        int viewWidth = 80,
        string? markdown = null,
        string? documentPath = null)
    {
        var list = new List<FormattedLine>
        {
            FormattedLine.Plain("", TuiPalette.BgPrimary),
            FormattedLine.WithBackground($"  \U0001f4cb  {title}", TuiPalette.ModePlanFg, TuiPalette.BgTerminalHeader),
            FormattedLine.Plain("", TuiPalette.BgPrimary),
        };
        if (!string.IsNullOrWhiteSpace(documentPath))
        {
            const string pathPrefix = "  📄 计划文档: ";
            var available = Math.Max(20, viewWidth - TextWidthHelper.GetDisplayWidth(pathPrefix));
            foreach (var line in TextWidthHelper.WordWrapByWidth(documentPath, available))
                list.Add(FormattedLine.Plain(pathPrefix + line, TuiPalette.FgMuted));
            list.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
        }
        // 完整计划全文渲染，让用户在 LLM 迭代计划时即可审阅；执行阶段不传 markdown 保持精简。
        // 辅助性说明（如操作引导）由 LLM 在对话流中提供，侧边栏只保留纯计划内容。
        if (!string.IsNullOrWhiteSpace(markdown))
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
        return list;
    }
}
