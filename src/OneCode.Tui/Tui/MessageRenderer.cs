namespace OneCode.Tui;

// 渲染辅助逻辑：纯函数，无实例状态依赖。

internal static class MessageRenderer
{
    /// <summary>
    /// 在展开/折叠时替换三角形符号。支持两种变体：
    /// 小三角（▸ ▾ U+25B8/U+25BE，工具行）和大三角（▶ ▼ U+25B6/U+25BC，思考块）。
    /// </summary>
    public static IReadOnlyList<LineSegment>? ReplaceTriangleSymbol(
        IReadOnlyList<LineSegment>? segments, bool collapsed)
    {
        if (segments is null) return null;
        var result = new LineSegment[segments.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (seg.Text.Contains('\u25b8') || seg.Text.Contains('\u25be')
                || seg.Text.Contains('\u25b6') || seg.Text.Contains('\u25bc')
                || seg.Text.Contains('\u25cf'))
            {
                var newText = collapsed
                    ? seg.Text
                        .Replace("\u25be", "\u25b8")
                        .Replace("\u25bc", "\u25b6")
                        .Replace("\u25cf", "\u25b8")
                    : seg.Text
                        .Replace("\u25b8", "\u25be")
                        .Replace("\u25b6", "\u25bc")
                        .Replace("\u25cf", "\u25be");
                result[i] = new LineSegment(newText, seg.Color, seg.Bg);
            }
            else
            {
                result[i] = seg;
            }
        }
        return result;
    }

    /// <summary>
    /// 构建工具调用展开后显示的完整详情行（Args + Result，按视口宽度自动换行）。
    /// 折叠行负责保持会话紧凑；用户主动展开后不换行重排，但结果正文有行数上限——
    /// 完整内容已发送给模型，对话流里只做概览。
    /// 组行（<see cref="ToolLineTag.Group"/> 非空）展开为逐条摘要列表：
    /// 序号 + 搜索词/路径等目标 + 结果摘要 + 单条时长。
    /// </summary>
    public static List<LineEntry> BuildToolDetailLines(ToolLineTag tag, int viewportWidth)
    {
        const string detailIndent = "      ";
        List<LineEntry> lines = [];
        var marker = new ToolDetailLineTag();
        var maxContentWidth = Math.Max(20, viewportWidth - detailIndent.Length);

        if (tag.Group is { Count: > 0 } group)
        {
            for (var i = 0; i < group.Count; i++)
            {
                var member = group[i];
                var target = TextWidthHelper.CollapseToSingleLine(
                    ToolResultSummarizer.FormatTarget(tag.Name, member.Args));
                var summary = string.IsNullOrEmpty(member.Result)
                    ? string.Empty
                    : TextWidthHelper.CollapseToSingleLine(
                        ToolResultSummarizer.Summarize(tag.Name, member.Result, member.Args) ?? string.Empty);

                var text = string.IsNullOrEmpty(summary)
                    ? $"{i + 1}. {target} ({member.Duration})"
                    : $"{i + 1}. {target} · {summary} ({member.Duration})";
                var entryColor = member.IsError ? TuiPalette.Error : TuiPalette.FgSecondary;
                lines.Add(new LineEntry(
                    $"{detailIndent}{text}",
                    entryColor,
                    new[]
                    {
                        new LineSegment($"{detailIndent}{i + 1}. ", TuiPalette.FgMuted),
                        new LineSegment(target, TuiPalette.ToolDetailColor),
                        new LineSegment(string.IsNullOrEmpty(summary) ? $" ({member.Duration})" : $" · {summary} ({member.Duration})", entryColor),
                    },
                    Tag: marker));
            }

            return lines;
        }

        // WebSearch/WebFetch 的展开详情头行已包含关键输入（query / prompt），
        // Args JSON 原文对用户是噪声，不再重复输出。
        if (!string.IsNullOrWhiteSpace(tag.Args)
            && ToolResultSummarizer.NormalizeName(tag.Name) is not ("websearch" or "webfetch"))
        {
            var prefixLen = detailIndent.Length + "Args: ".Length;
            var argsMaxWidth = Math.Max(20, viewportWidth - prefixLen);
            var displayArgs = OneCode.Core.Tools.DisplayJsonSerializer.NormalizeForDisplay(tag.Args, writeIndented: false);
            var wrappedArgs = TextWidthHelper.WordWrapByWidth(displayArgs, argsMaxWidth);
            for (var i = 0; i < wrappedArgs.Count; i++)
            {
                if (i == 0)
                {
                    lines.Add(new LineEntry(
                        $"{detailIndent}Args: {wrappedArgs[i]}",
                        TuiPalette.ToolDetailColor,
                        new[] { new LineSegment($"{detailIndent}Args: ", TuiPalette.FgMuted), new LineSegment(wrappedArgs[i], TuiPalette.ToolDetailColor) },
                        Tag: marker));
                }
                else
                {
                    var continuationIndent = detailIndent + "      ";
                    lines.Add(new LineEntry(
                        $"{continuationIndent}{wrappedArgs[i]}",
                        TuiPalette.ToolDetailColor,
                        new[] { new LineSegment($"{continuationIndent}", TuiPalette.FgMuted), new LineSegment(wrappedArgs[i], TuiPalette.ToolDetailColor) },
                        Tag: marker));
                }
            }
        }

        // 运行中的子代理 fork 行：展开附带实时统计（与活动行同一格式化源，内容一致）。
        if (tag.LiveProgress is { } live)
        {
            lines.Add(new LineEntry(
                $"{detailIndent}{FormatSubagentProgress(live)}",
                TuiPalette.FgMuted,
                null,
                Tag: marker));
        }

        if (!string.IsNullOrWhiteSpace(tag.Result))
        {
            foreach (var detail in ToolResultDetailFormatter.FormatDetail(tag.Name, tag.Result))
            {
                foreach (var wrappedLine in TextWidthHelper.WordWrapByWidth(detail.Text, maxContentWidth))
                {
                    lines.Add(new LineEntry(
                        $"{detailIndent}{wrappedLine}",
                        detail.Fg ?? TuiPalette.FgSecondary,
                        null,
                        Tag: marker));
                }
            }
        }

        if (lines.Count == 0)
        {
            lines.Add(new LineEntry(
                $"{detailIndent}(no details available)",
                TuiPalette.FgMuted,
                null,
                Tag: marker));
        }

        return lines;
    }

    /// <summary>
    /// 格式化子代理实时进度统计（阶段 · 轮次/调用数/当前工具/耗时）。
    /// 活动行与展开详情共用同一格式化源，保证两处文案一致。
    /// </summary>
    internal static string FormatSubagentProgress(SubagentProgressEvent progress)
    {
        var parts = new List<string>
        {
            progress.Phase switch
            {
                SubagentProgressPhase.Completed => "已完成",
                SubagentProgressPhase.Failed => "失败",
                _ => "运行中",
            },
        };
        if (progress.TurnCount > 0)
            parts.Add($"turn {progress.TurnCount}");
        if (progress.ToolCallCount > 0)
            parts.Add($"{progress.ToolCallCount} 次工具调用");
        if (!string.IsNullOrEmpty(progress.CurrentToolName))
            parts.Add($"{progress.CurrentToolName}…");
        if (progress.ElapsedMs > 0)
            parts.Add($"已耗时 {StreamingToolTracker.FormatElapsed(TimeSpan.FromMilliseconds(progress.ElapsedMs))}");
        return string.Join(" · ", parts);
    }
}
