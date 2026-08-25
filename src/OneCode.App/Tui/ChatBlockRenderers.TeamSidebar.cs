namespace OneCode.App.Tui;

/// <summary>
/// TEAM 侧边栏渲染（纯函数）：把 <see cref="TeamSidebarContent"/> 投影为
/// FormattedLine 序列。只输出状态事实，禁止出现任何键位提示/操作引导文字——
/// 侧边栏是纯显示面板，交互全部发生在左侧对话列（测试断言守护此约束）。
/// </summary>
public static partial class ChatBlockRenderers
{
    /// <summary>侧边栏内容行数上限（超出依赖 MessageListView 滚轮滚动）。</summary>
    private const int TeamSidebarMaxRows = 200;

    public static IReadOnlyList<FormattedLine> RenderTeamSidebar(TeamSidebarContent content, int width)
    {
        var lines = new List<FormattedLine> { FormattedLine.Plain("", TuiPalette.BgPrimary) };
        void Add(FormattedLine line)
        {
            if (lines.Count < TeamSidebarMaxRows) lines.Add(line);
        }

        if (content.Tasks.Count > 0)
        {
            AddSection(lines, "任务");
            foreach (var task in content.Tasks)
                Add(TaskRow(task, width));
        }

        if (content.Decisions.Count > 0)
        {
            AddSection(lines, "决策记录");
            foreach (var decision in content.Decisions)
                Add(FormattedLine.FromSegments(
                [
                    new("  ✓ ", TuiPalette.Success),
                    new(TruncateToFit(decision.Answer, Math.Max(8, width - 5)), TuiPalette.FgPrimary),
                ]));
        }

        if (content.Files.Count > 0)
        {
            AddSection(lines, $"文件变更 ({content.Files.Count})");
            foreach (var file in content.Files)
                Add(FileRow(file, width));
        }

        if (content.Gates.Count > 0)
        {
            AddSection(lines, "质量门禁");
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
            AddSection(lines, "关键节点");
            foreach (var milestone in content.Milestones)
                Add(FormattedLine.Plain($"  {TruncateToFit(milestone, Math.Max(8, width - 3))}", TuiPalette.FgMuted));
        }

        return lines;
    }

    private static void AddSection(List<FormattedLine> lines, string title)
    {
        lines.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
        lines.Add(FormattedLine.Plain(TruncateToFit($"── {title}", 40), TuiPalette.FgMuted));
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

    private static string TruncateToFit(string text, int maxWidth)
        => text.Length <= maxWidth ? text : text[..Math.Max(1, maxWidth - 1)] + "…";
}
