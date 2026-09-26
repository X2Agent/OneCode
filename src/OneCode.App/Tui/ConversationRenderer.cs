namespace OneCode.App.Tui;

// 渲染辅助逻辑：纯函数 + 共享常量，无实例状态依赖。

internal static class ConversationRenderer
{
    public static readonly int ContentIndent = TuiSpacing.MessageContentIndent;
    public static readonly string Indent = new(' ', ContentIndent);

    /// <summary>
    /// 错误摘要的显示宽度预算：首行超出则截断，详情从首行起完整展示。
    /// RenderErrorBlock 与 ToggleErrorExpansion 共用，保证两处 startIdx 判定一致。
    /// </summary>
    public static int ErrorSummaryBudget(int contentWidth)
        => Math.Max(1, Math.Max(20, contentWidth - 6) - 6);

    /// <summary>
    /// 构建已完成工具调用的格式化行（工具名 + 目标 + 结果摘要 + 耗时）。
    /// <paramref name="maxWidth"/> &gt; 0 时按显示宽度（CJK 感知）截断中间内容段
    /// （目标 / 结果摘要），保证整行不超出视口宽度（尾部预留 1 列滚动条），
    /// 避免长参数被终端裁切；目标段优先占用预算，摘要使用剩余空间。
    /// </summary>
    public static FormattedLine MakeCompletedToolLine(
        string name, bool isError, string? toolInput, string? duration, string? result = null, string? agentName = null, int maxWidth = 0)
    {
        var statusColor = isError ? TuiPalette.Error : TuiPalette.Success;

        // 尾部状态段宽度固定，先计算出来，从中间内容段的截断预算中扣除。
        var statusText = isError
            ? (string.IsNullOrEmpty(duration) ? " \u00b7 error" : $" \u00b7 {duration}")
            : string.IsNullOrEmpty(duration) ? string.Empty : $" \u00b7 {duration}";

        List<LineSegment> segments = [
            new($"{Indent}", TuiPalette.BgPrimary),
            new($"{TuiGlyphs.ToolCall} ", TuiPalette.Accent),
            new(name, TuiPalette.Warning),
        ];
        // TEAM 归属前缀：显示执行该工具调用的成员 ID（角色专属色）。
        if (!string.IsNullOrWhiteSpace(agentName))
            segments.Add(new($" [{agentName}]", TuiPalette.FromAgentName(agentName)));

        // 使用 ToolResultSummarizer 格式化目标（文件路径、命令等）
        // 折叠行按单行布局绘制，目标/摘要含真实换行会令整行错位，先压成单行。
        var target = TextWidthHelper.CollapseToSingleLine(
            ToolResultSummarizer.FormatTarget(name, toolInput));
        var summary = !isError && !string.IsNullOrEmpty(result)
            ? TextWidthHelper.CollapseToSingleLine(ToolResultSummarizer.Summarize(name, result, toolInput) ?? string.Empty)
            : string.Empty;

        if (maxWidth > 0)
        {
            var fixedWidth = segments.Sum(s => TextWidthHelper.GetDisplayWidth(s.Text))
                + TextWidthHelper.GetDisplayWidth(statusText)
                + 1; // 尾部滚动条列
            var budget = Math.Max(0, maxWidth - fixedWidth);

            if (!string.IsNullOrWhiteSpace(target))
            {
                // "-1" 为目标段前导空格；仅在超宽时截断（TruncateByWidth 为省略号
                // 预留 1 列，恰宽文本传入会被误截）。
                var targetAvailable = budget - 1;
                if (targetAvailable > 0 && TextWidthHelper.GetDisplayWidth(target) > targetAvailable)
                    target = TextWidthHelper.TruncateByWidth(target, targetAvailable);
                if (TextWidthHelper.GetDisplayWidth(target) <= Math.Max(0, targetAvailable))
                {
                    segments.Add(new($" {target}", TuiPalette.ToolDetailColor));
                    budget -= TextWidthHelper.GetDisplayWidth(target) + 1;
                }
            }

            if (!string.IsNullOrEmpty(summary))
            {
                // "-3" 为 " · " 分隔符；同样仅在超宽时截断。
                var summaryAvailable = budget - 3;
                if (summaryAvailable > 0 && TextWidthHelper.GetDisplayWidth(summary) > summaryAvailable)
                    summary = TextWidthHelper.TruncateByWidth(summary, summaryAvailable);
                if (TextWidthHelper.GetDisplayWidth(summary) <= Math.Max(0, summaryAvailable))
                    segments.Add(new($" \u00b7 {summary}", statusColor));
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(target))
                segments.Add(new($" {target}", TuiPalette.ToolDetailColor));
            if (!string.IsNullOrEmpty(summary))
                segments.Add(new($" \u00b7 {summary}", statusColor));
        }

        if (!string.IsNullOrEmpty(duration))
            segments.Add(new($" \u00b7 {duration}", TuiPalette.FgMuted));
        else if (isError)
            segments.Add(new(" \u00b7 error", statusColor));

        var tag = new ToolLineTag(name, toolInput, result, IsExpanded: false);
        return FormattedLine.FromSegmentsWithTag(segments.ToArray(), tag);
    }

    /// <summary>构建流式通知行（横线分隔 + 文本）。</summary>
    public static FormattedLine MakeStreamingNotice(string text, Color color)
        => FormattedLine.FromSegments(new[]
        {
            new LineSegment($"{Indent}", TuiPalette.BgPrimary),
            new LineSegment($"{TuiGlyphs.BorderHorizontal} ", TuiPalette.FgMuted),
            new LineSegment(text, color),
        });

    /// <summary>按显示宽度换行文本（处理 CJK 双宽字符）。</summary>
    public static List<string> WordWrapStreaming(string text, int maxWidth)
        => TextWidthHelper.WordWrapByWidth(text, maxWidth);
}
