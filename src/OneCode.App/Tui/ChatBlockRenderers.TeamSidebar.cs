namespace OneCode.App.Tui;

/// <summary>
/// TEAM 侧边栏渲染（纯函数）：把 <see cref="TeamSidebarContent"/> 投影为
/// FormattedLine 序列。只输出状态事实，禁止出现任何键位提示/操作引导文字——
/// 侧边栏是纯显示面板，交互全部发生在左侧对话列（测试断言守护此约束）。
///
/// 可读性原则：每个分区标题带图标与汇总计数（任务 x/y、门禁 通过/总数），
/// 用户扫一眼标题行即可掌握整体进度；任务行按「进行中 → 待执行 → 失败 →
/// 已完成」排序，把需要关注的内容顶到最前；截断一律按显示宽度（CJK 感知）。
/// </summary>
public static partial class ChatBlockRenderers
{
    /// <summary>侧边栏内容行数上限（超出依赖 MessageListView 滚轮滚动）。</summary>
    private const int TeamSidebarMaxRows = 200;

    public static IReadOnlyList<FormattedLine> RenderTeamSidebar(TeamSidebarContent content, int width)
    {
        List<FormattedLine> lines = [ FormattedLine.Plain("", TuiPalette.BgPrimary) ];
        void Add(FormattedLine line)
        {
            if (lines.Count < TeamSidebarMaxRows) lines.Add(line);
        }

        if (content.Tasks.Count > 0)
        {
            var done = content.Tasks.Count(t => t.Status is "done" or "failed");
            AddSection(lines, $"\U0001f4cb 任务 ({done}/{content.Tasks.Count})", width);
            // 重点前置：进行中 → 待执行 → 失败 → 已完成（同层级保持事件顺序）。
            foreach (var task in content.Tasks.OrderBy(t => t.Status switch
                     {
                         "running" => 0,
                         "pending" => 1,
                         "failed" => 2,
                         _ => 3,
                     }))
                Add(TaskRow(task, width));
        }

        if (content.Decisions.Count > 0)
        {
            AddSection(lines, $"\U0001f4ac 澄清决策 ({content.Decisions.Count})", width);
            for (var i = 0; i < content.Decisions.Count; i++)
            {
                var answer = content.Decisions[i].Answer;
                Add(FormattedLine.FromSegments(
                [
                    new($"  {i + 1}. ", TuiPalette.Success),
                    new(TruncateToFit(answer, Math.Max(8, width - 6)), TuiPalette.FgPrimary),
                ]));
            }
        }

        if (content.Files.Count > 0)
        {
            AddSection(lines, $"\U0001f4dd 文件变更 ({content.Files.Count})", width);
            foreach (var file in content.Files)
                Add(FileRow(file, width));
        }

        if (content.Gates.Count > 0)
        {
            var passed = content.Gates.Count(g => g.Status == "Passed");
            AddSection(lines, $"\U0001f6a6 质量门禁 ({passed}/{content.Gates.Count} 通过)", width);
            foreach (var gate in content.Gates)
            {
                var (icon, color) = GateIndicator(gate.Status);
                Add(FormattedLine.FromSegments(
                [
                    new($"  {icon} ", color),
                    new(TruncateToFit(gate.Name, Math.Max(8, width - 5)), TuiPalette.FgPrimary),
                ]));
            }
        }

        if (content.Milestones.Count > 0)
        {
            AddSection(lines, $"\U0001f3c1 关键节点 ({content.Milestones.Count})", width);
            foreach (var milestone in content.Milestones)
                Add(FormattedLine.Plain($"  {TruncateToFit(milestone, Math.Max(8, width - 3))}", TuiPalette.FgMuted));
        }

        return lines;
    }

    private static void AddSection(List<FormattedLine> lines, string title, int width)
    {
        lines.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
        lines.Add(FormattedLine.Plain(TruncateToFit($"── {title}", Math.Max(12, width)), TuiPalette.FgSecondary));
    }

    private static FormattedLine TaskRow(TeamSidebarTask task, int width)
    {
        var (icon, color) = task.Status switch
        {
            "running" => ("⠋", TuiPalette.Warning),
            "done" => ("✓", TuiPalette.Success),
            "failed" => ("✗", TuiPalette.Error),
            _ => ("·", TuiPalette.FgMuted),
        };

        var rolePrefix = string.IsNullOrWhiteSpace(task.Role) ? "" : $"[{task.Role}] ";
        var progressSuffix = task.Completed is { } c && task.Total is { } t ? $" {c}/{t}" : "";
        var text = $"{rolePrefix}{task.Title}{progressSuffix}";
        return FormattedLine.FromSegments(
        [
            new($"  {icon} ", color),
            new(TruncateToFit(text, Math.Max(8, width - 5)), TuiPalette.FgPrimary),
        ]);
    }

    private static FormattedLine FileRow(TeamSidebarFile file, int width)
    {
        var counts = $"+{file.Added} -{file.Removed}";
        var contributor = string.IsNullOrEmpty(file.Contributors) ? "" : $"  ⌥{file.Contributors}";
        var nameBudget = Math.Max(8, width - counts.Length - contributor.Length - 4);
        return FormattedLine.FromSegments(
        [
            new($"  {TruncateToFit(file.FileName, nameBudget)}", TuiPalette.FgPrimary),
            new($" {counts}", TuiPalette.Accent),
            new(contributor, TuiPalette.FgMuted),
        ]);
    }

    private static (string Icon, Terminal.Gui.Drawing.Color Color) GateIndicator(string status) => status switch
    {
        "Passed" => ("✓", TuiPalette.Success),
        "Failed" => ("✗", TuiPalette.Error),
        "Pending" => ("·", TuiPalette.FgMuted),
        "SkippedByDependency" => ("○", TuiPalette.FgMuted),
        _ => ("⠋", TuiPalette.Warning),
    };

    /// <summary>按显示宽度截断（CJK/emoji 双宽感知），超宽时以省略号结尾。</summary>
    private static string TruncateToFit(string text, int maxWidth)
        => TextWidthHelper.TruncateByWidth(text, Math.Max(0, maxWidth));
}
