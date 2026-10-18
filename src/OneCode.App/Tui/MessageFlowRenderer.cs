namespace OneCode.App.Tui;

/// <summary>
/// Message renderer — chat content format:
///
///   [U] 帮我重构 StartupFlowCoordinator                    13:14
///
///   [A] general-purpose                              13:14
///     ● 分析中...
///     当前耦合了 Terminal.Gui 创建逻辑...
///
///   [E] Explore                                       13:14
///     ⚡ Read  StartupFlowCoordinator.cs             ✓
///     ⚡ Grep  TuiHost|Terminal.Gui                  ✓
///
/// Uses FormattedLine segments for multi-color per-line rendering.
/// </summary>
public sealed class MessageFlowRenderer
{
    public int CurrentWidth { get; set; } = 80;
    public bool ShowThinking { get; set; } = false;
    /// <summary>
    /// 实时委托：返回当前是否展开显示思考块。优先于 <see cref="ShowThinking"/> 属性，
    /// 使 <c>/think show|hide</c> 命令修改 AppState 后立即影响后续渲染，无需手动同步。
    /// 为 null 时回退到 <see cref="ShowThinking"/> 属性（测试场景）。
    /// </summary>
    public Func<bool>? GetShowThinking { get; set; }
    public WorkingMode CurrentMode { get; set; } = WorkingMode.Build;

    private const int ContentIndent = TuiSpacing.MessageContentIndent;
    private const int MaxToolResultLines = 20;
    private static readonly string Indent = new(' ', ContentIndent);

    public IReadOnlyList<FormattedLine> RenderMessage(Message message)
    {
        return message switch
        {
            UserMessage um => RenderUserMessage(um),
            AssistantMessage am => RenderAssistantMessage(am),
            ToolResultMessage trm => RenderToolResultMessage(trm),
            SystemMessage sm => RenderSystemMessage(sm),
            _ => new[] { FormattedLine.Plain($"[Unknown: {message.GetType().Name}]", TuiPalette.Error) }
        };
    }

    /// <summary>
    /// 把消息流切分为渲染单元：<see cref="ToolRunSegment"/>（连续工具活动段）或单条
    /// <see cref="Message"/>。工具段以"含 ToolUseBlock 的 assistant"开始，延伸到后续的
    /// ToolResultMessage / 更多工具 assistant，遇到纯文本 assistant 或非工具消息封段——
    /// 渲染时段内相邻同名工具对聚合为组行（与流内合并形态一致，防止单轮多工具刷屏）。
    /// </summary>
    public static IReadOnlyList<object> SegmentConversation(IReadOnlyList<Message> messages)
    {
        List<object> units = [];
        var i = 0;
        while (i < messages.Count)
        {
            if (messages[i] is AssistantMessage first && first.Content.Any(static block => block is ToolUseBlock))
            {
                var segment = new ToolRunSegment();
                while (i < messages.Count)
                {
                    switch (messages[i])
                    {
                        case AssistantMessage am when am.Content.Any(static block => block is ToolUseBlock):
                            segment.Assistants.Add(am);
                            i++;
                            break;
                        case ToolResultMessage trm:
                            segment.Results.Add(trm);
                            i++;
                            break;
                        default:
                            // 纯文本 assistant / 用户 / 系统消息——封段。
                            goto SegmentEnd;
                    }
                }
                SegmentEnd:
                units.Add(segment);
            }
            else
            {
                units.Add(messages[i]);
                i++;
            }
        }

        return units;
    }

    /// <summary>连续工具活动段：若干工具 assistant + 按序跟随的工具结果。</summary>
    public sealed record ToolRunSegment
    {
        public List<AssistantMessage> Assistants { get; init; } = [];
        public List<ToolResultMessage> Results { get; init; } = [];
    }

    /// <summary>
    /// 渲染工具活动段：相邻同名 (tool_use, result) 对聚合成组行（×N，可展开逐条），
    /// 异名工具/文本块打断聚合；单条对渲染为与流内一致的完成行（可展开详情）。
    /// </summary>
    public IReadOnlyList<FormattedLine> RenderToolRunSegment(ToolRunSegment segment)
    {
        List<FormattedLine> lines = [];
        var pendingCalls = new Dictionary<string, (string Name, string Input)>(StringComparer.Ordinal);
        string? runName = null;
        List<ToolGroupEntry> runEntries = [];
        // 局部队列消费：segment 是 journal 捕获的共享对象（resize 重渲会再次调用本方法），
        // 渲染过程不得修改其 Results 列表。
        var remainingResults = new Queue<ToolResultMessage>(segment.Results);

        void FlushRun()
        {
            if (runName is null || runEntries.Count == 0)
            {
                runName = null;
                runEntries = [];
                return;
            }

            var last = runEntries[^1];
            // 单条 run 不挂 Group：展开保持完整 Args/Result 详情；≥2 条才是组行。
            lines.Add(ConversationRenderer.MakeCompletedToolLine(
                runName, last.IsError, last.Args, null, last.Result, maxWidth: CurrentWidth,
                groupCount: runEntries.Count,
                group: runEntries.Count > 1 ? runEntries : null));
            runName = null;
            runEntries = [];
        }

        foreach (var assistant in segment.Assistants)
        {
            // 文本/思考块先行（与块顺序一致），工具块进入聚合缓冲。
            foreach (var block in assistant.Content)
            {
                switch (block)
                {
                    case TextBlock tb when !string.IsNullOrEmpty(tb.Text):
                        RenderMarkdownBlock(lines, tb.Text);
                        break;
                    case ThinkingBlock thb:
                        RenderThinkingBlock(lines, thb);
                        break;
                    case ToolUseBlock tub when tub.Id is { Length: > 0 } id:
                        pendingCalls[id] = (tub.Name, tub.Input);
                        break;
                }
            }

            // 本条 assistant 的工具结果按序配对消费；id 未登记的（后续 assistant 的
            // 结果）留待其 assistant 处理时配对。
            while (remainingResults.Count > 0
                   && pendingCalls.Remove(remainingResults.Peek().ToolUseId, out var call))
            {
                var result = remainingResults.Dequeue();
                if (runName is { } current && current != call.Name)
                    FlushRun();
                runName ??= call.Name;
                runEntries.Add(new ToolGroupEntry(call.Input, result.Content, string.Empty, result.IsError));
            }
        }

        FlushRun();

        // 未配对的 result（孤儿防御）与 tool_use（中断残留）按现状形态兜底渲染。
        foreach (var orphan in remainingResults)
            lines.AddRange(RenderToolResultMessage(orphan));
        foreach (var (_, (name, input)) in pendingCalls)
            lines.Add(MakeToolLine(name, input, null));

        return lines;
    }

    // message renderers

    private IReadOnlyList<FormattedLine> RenderUserMessage(UserMessage msg)
    {
        var time = msg.Timestamp.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        List<FormattedLine> lines = [];
        var barColor = TuiPalette.GetModeBarColor(CurrentMode);
        var timeStr = $"{time}";
        var reservedRightWidth = TuiSpacing.MessageTimestampRightPadding;
        var firstLineWidth = Math.Max(
            1,
            CurrentWidth - reservedRightWidth - ContentIndent - timeStr.Length - 1);
        var wrapped = WordWrap(msg.Content, firstLineWidth);
        var isFirstLine = true;

        foreach (var word in wrapped)
        {
            if (word == "\n")
            {
                if (isFirstLine)
                {
                    lines.Add(RenderUserHeader("", timeStr, barColor, reservedRightWidth));
                    isFirstLine = false;
                }
                else
                {
                    lines.Add(FormattedLine.Plain("", TuiPalette.BgPrimary));
                }
                continue;
            }

            if (isFirstLine)
            {
                lines.Add(RenderUserHeader(word, timeStr, barColor, reservedRightWidth));
                isFirstLine = false;
                continue;
            }

            lines.Add(FormattedLine.FromSegments(new[]
            {
                new LineSegment(TuiGlyphs.BarQuote, barColor),
                new LineSegment($" {word}", TuiPalette.FgPrimary),
            }));
        }

        if (isFirstLine)
            lines.Add(RenderUserHeader("", timeStr, barColor, reservedRightWidth));

        return lines;
    }

    private FormattedLine RenderUserHeader(
        string content,
        string time,
        Color barColor,
        int reservedRightWidth)
    {
        // CJK 内容显示宽度大于字符数，pad 必须按显示宽度计算，
        // 否则中文首行的时间戳错位甚至整行溢出视口。
        var headerPad = Math.Max(
            1,
            CurrentWidth - reservedRightWidth - ContentIndent
            - TextWidthHelper.GetDisplayWidth(content) - time.Length);

        return FormattedLine.FromSegments(new[]
        {
            new LineSegment(TuiGlyphs.BarQuote, barColor),
            new LineSegment($" {content}", TuiPalette.FgPrimary),
            new LineSegment(new string(' ', headerPad), TuiPalette.BgPrimary),
            new LineSegment(time, TuiPalette.FgMuted),
        });
    }

    private IReadOnlyList<FormattedLine> RenderAssistantMessage(AssistantMessage msg)
    {
        List<FormattedLine> lines = [];

        foreach (var block in msg.Content)
        {
            switch (block)
            {
                case TextBlock tb when !string.IsNullOrEmpty(tb.Text):
                    // Route assistant text through the Markdown renderer so
                    // code blocks, headings, lists, and tables render properly.
                    // 回放路径与流式路径同样剥掉回复起点的模型前导缩进。
                    RenderMarkdownBlock(lines, MessageFlowRenderer.TrimReplyLeadingWhitespace(tb.Text));
                    break;
                case ToolUseBlock tub:
                    lines.Add(MakeToolLine(tub.Name, null, null));
                    break;
                case ThinkingBlock thb:
                    RenderThinkingBlock(lines, thb);
                    break;
            }
        }

        return lines;
    }

    /// <summary>
    /// Appends assistant text content to the given line list, using
    /// the markdown renderer so code blocks, headings, lists, and tables
    /// render properly.
    /// 
    /// This is the public entry point used by <see cref="ChatTranscriptView"/>
    /// to commit the final rendered text after streaming completes, ensuring
    /// markdown syntax is rendered properly rather than left as raw
    /// word-wrapped text.
    /// </summary>
    public void AppendAssistantText(List<FormattedLine> lines, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        RenderMarkdownBlock(lines, text);
    }

    /// <summary>
    /// 去掉一段回复文本开头的空格/制表符。部分模型在 reasoning/text 交错的
    /// 流式输出里会给文本段带上大段前导缩进，原样渲染会让整段看起来居中。
    /// 只处理回复开头——正文与代码块内部的缩进是有语义的，不动。
    /// </summary>
    public static string TrimReplyLeadingWhitespace(string text)
    {
        return text.TrimStart(' ', '\t');
    }

    /// <summary>
    /// Renders a block of markdown text into the message's FormattedLine list.
    /// Falls back to plain wrapped text if the markdown is trivial (single
    /// line, no markdown syntax) to avoid unnecessary overhead and preserve
    /// the original look for short replies.
    /// </summary>
    private void RenderMarkdownBlock(List<FormattedLine> lines, string text)
    {
        // Fast path: single-line, no markdown punctuation → wrap as before.
        // This keeps short assistant replies rendering identically to the
        // pre-markdown behaviour and avoids Markdig parse cost.
        var hasMarkdown = text.IndexOfAny(['#', '*', '`', '>', '-', '|', '[']) >= 0
            || text.Contains("\n\n");
        if (!hasMarkdown)
        {
            RenderWrapped(lines, text, TuiPalette.AssistantMessage);
            return;
        }

        try
        {
            var convLines = MarkdownRenderer.Render(text, CurrentWidth);
            foreach (var cl in convLines)
            {
                var color = cl.Role switch
                {
                    LineRole.System => TuiPalette.Accent,
                    LineRole.Error => TuiPalette.Error,
                    LineRole.DiffAdded => TuiPalette.DiffAdded,
                    LineRole.DiffRemoved => TuiPalette.DiffRemoved,
                    LineRole.DiffHunk => TuiPalette.FgMuted,
                    _ => TuiPalette.AssistantMessage,
                };

                // If the ConvLine carries multi-color segments (e.g. code block
                // header with language label + copy icon), use FromSegmentsWithTag.
                if (cl.Segments is { Count: > 0 } segments)
                {
                    if (cl.Tag is not null)
                        lines.Add(FormattedLine.FromSegmentsWithTag(segments.ToArray(), cl.Tag));
                    else
                        lines.Add(FormattedLine.FromSegments(segments.ToArray()));
                }
                else if (cl.Bg is { } bg)
                {
                    lines.Add(FormattedLine.WithBackground(cl.Text, color, bg));
                }
                else
                {
                    lines.Add(FormattedLine.Plain(cl.Text, color));
                }
            }
        }
        catch
        {
            // If Markdig chokes on malformed markdown, fall back to plain text
            // so the message still renders.
            RenderWrapped(lines, text, TuiPalette.AssistantMessage);
        }
    }

    private IReadOnlyList<FormattedLine> RenderToolResultMessage(ToolResultMessage msg)
    {
        List<FormattedLine> lines = [];

        // Show tool name + status icon so the result is visually linked to its tool call.
        lines.Add(MakeToolLine(msg.ToolName, null, !msg.IsError));

        // 与流式展开详情同一格式化源：信封解包 + 按工具渲染（WebSearch 列表等）。
        var detail = ToolResultDetailFormatter.FormatDetail(msg.ToolName, msg.Content);
        var totalLines = detail.Count;

        if (totalLines <= MaxToolResultLines)
        {
            foreach (var d in detail)
                lines.Add(FormattedLine.Plain($"{Indent}  {TruncateLine(d.Text)}", d.Fg ?? TuiPalette.FgSecondary));
        }
        else
        {
            var preview = Math.Min(3, MaxToolResultLines / 2);
            for (var i = 0; i < preview; i++)
                lines.Add(FormattedLine.Plain($"{Indent}  {TruncateLine(detail[i].Text)}", detail[i].Fg ?? TuiPalette.FgSecondary));
            lines.Add(FormattedLine.Plain(
                $"{Indent}  \u2026 ({totalLines - preview} more lines)",
                TuiPalette.FgMuted));
        }

        return lines;
    }

    private IReadOnlyList<FormattedLine> RenderSystemMessage(SystemMessage msg)
    {
        List<FormattedLine> lines = [];
        var availableWidth = Math.Max(20, CurrentWidth - ContentIndent - 2);
        var isFirstLine = true;

        // StringReader.ReadLine() automatically handles \r\n, \n, and \r across platforms
        using var reader = new StringReader(msg.Content);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) is not null)
        {
            var wrapped = WordWrap(rawLine, availableWidth);

            if (wrapped.Count == 0)
            {
                lines.Add(FormattedLine.Plain(
                    isFirstLine ? $"{Indent}{TuiGlyphs.BorderHorizontal}" : "",
                    TuiPalette.SystemMessage));
                isFirstLine = false;
                continue;
            }

            for (var j = 0; j < wrapped.Count; j++)
            {
                if (isFirstLine && j == 0)
                {
                    lines.Add(FormattedLine.Plain($"{Indent}{TuiGlyphs.BorderHorizontal} {wrapped[j]}", TuiPalette.SystemMessage));
                }
                else
                {
                    lines.Add(FormattedLine.Plain($"{Indent}{wrapped[j]}", TuiPalette.SystemMessage));
                }
            }

            isFirstLine = false;
        }

        if (lines.Count == 0)
        {
            lines.Add(FormattedLine.Plain($"{Indent}{TuiGlyphs.BorderHorizontal}", TuiPalette.SystemMessage));
        }

        return lines;
    }

    private void RenderThinkingBlock(List<FormattedLine> lines, ThinkingBlock thb)
    {
        var thinking = thb.Thinking;
        if (string.IsNullOrEmpty(thinking)) return;

        var showThinking = GetShowThinking?.Invoke() ?? ShowThinking;
        if (!showThinking)
        {
            var collapsedTag = new ThinkingLineTag(thinking, IsExpanded: false);
            lines.Add(FormattedLine.FromSegmentsWithTag(new[]
            {
                new LineSegment($"{Indent}", TuiPalette.BgPrimary),
                new LineSegment($"{TuiGlyphs.Collapsed} Thought:", TuiPalette.FgMuted),
            }, collapsedTag));
            return;
        }

        var expandedTag = new ThinkingLineTag(thinking, IsExpanded: true);
        lines.Add(FormattedLine.FromSegmentsWithTag(new[]
        {
            new LineSegment($"{Indent}", TuiPalette.BgPrimary),
            new LineSegment($"{TuiGlyphs.Expanded} Thought:", TuiPalette.FgMuted),
        }, expandedTag));
        var maxWidth = Math.Max(20, CurrentWidth - ContentIndent - 2);
        var thinkingLines = thinking.Replace("\r\n", "\n").Split('\n');
        foreach (var line in thinkingLines)
        {
            foreach (var w in WordWrap(line, maxWidth))
                lines.Add(FormattedLine.Plain($"{Indent}  {w}", TuiPalette.FgSecondary));
        }
    }

    /// <summary>Makes a tool-call inline row: ▸ Name  args   ✓/✗/◈
    /// Uses a right-pointing triangle (▸) — clickable to expand/collapse tool details.
    /// <paramref name="maxWidth"/> &gt; 0 时按显示宽度截断 args 段，防止长参数溢出视口被裁切。
    /// <paramref name="liveProgress"/> 非空时写入 tag，展开运行中的子代理行可见实时统计。</summary>
    internal static FormattedLine MakeToolLine(string name, string? args, bool? ok, string? duration = null, string? result = null, string? agentName = null, int maxWidth = 0, string? detailArgs = null, TuiSubagentProgress? liveProgress = null)
    {
        List<LineSegment> segments = [
            new($"{Indent}", TuiPalette.BgPrimary),
            new($"{TuiGlyphs.ToolCall} ", TuiPalette.Accent),
            new(name, TuiPalette.Warning),
        ];
        // 代理归属前缀：显示执行该工具调用的代理 ID（角色专属色）。
        if (!string.IsNullOrWhiteSpace(agentName))
            segments.Add(new($" [{agentName}]", TuiPalette.FromAgentName(agentName)));

        // 尾部状态段宽度固定，先算出来，从 args 段的截断预算中扣除。
        var suffix = ok switch
        {
            true => string.IsNullOrEmpty(duration) ? " \u00b7 完成" : $" \u00b7 {duration}",
            false => string.IsNullOrEmpty(duration) ? " \u00b7 错误" : $" \u00b7 {duration}",
            _ => string.Empty,
        };

        if (!string.IsNullOrWhiteSpace(args))
        {
            // 折叠行按单行布局绘制：参数里的真实换行/制表符必须先压成空格，
            // 否则测量宽度与实际绘制不一致，整行错位。展开详情保留真实换行。
            var displayArgs = TextWidthHelper.CollapseToSingleLine(args);
            if (maxWidth > 0)
            {
                var fixedWidth = segments.Sum(s => TextWidthHelper.GetDisplayWidth(s.Text))
                    + TextWidthHelper.GetDisplayWidth(suffix)
                    + 1; // 尾部滚动条列
                // "-3" 为 " · " 分隔符；仅在超宽时截断（TruncateByWidth 为省略号
                // 预留 1 列，恰宽文本传入会被误截）；空间不足时整段省略。
                var argsAvailable = Math.Max(0, maxWidth - fixedWidth) - 3;
                if (argsAvailable > 0 && TextWidthHelper.GetDisplayWidth(displayArgs) > argsAvailable)
                    displayArgs = TextWidthHelper.TruncateByWidth(displayArgs, argsAvailable);
                else if (argsAvailable <= 0)
                    displayArgs = string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(displayArgs))
                segments.Add(new($" \u00b7 {displayArgs}", TuiPalette.ToolDetailColor));
        }

        if (ok is true)
            segments.Add(new(suffix, TuiPalette.Success));
        else if (ok is false)
            segments.Add(new(suffix, TuiPalette.Error));
        // detailArgs 为空串时抑制详情行（live 进度行的标题即进度文本，重复展示为
        // "Args: ..." 反而误导）；null 时沿用 args（工具启动/完成行的既有语义）。
        var tag = new ToolLineTag(name, detailArgs ?? args, result, IsExpanded: false, LiveProgress: liveProgress);
        return FormattedLine.FromSegmentsWithTag(segments.ToArray(), tag);
    }

    private void RenderWrapped(List<FormattedLine> lines, string text, Color color)
    {
        // Pass the raw width to WordWrapByWidth — it defaults to 40 when
        // maxWidth <= 0 (e.g., in test contexts where Viewport.Width is 0).
        // This keeps the committed-text path consistent with the streaming
        // preview path, which also relies on WordWrapByWidth's default.
        var availableWidth = CurrentWidth - ContentIndent;
        var wrapped = WordWrap(text, availableWidth);

        foreach (var word in wrapped)
        {
            if (word == "\n")
            {
                lines.Add(FormattedLine.Plain("", color));
                continue;
            }
            lines.Add(FormattedLine.Plain($"{Indent}{word}", color));
        }
    }

    private string TruncateLine(string line)
    {
        var maxLen = CurrentWidth - ContentIndent - 2;
        if (maxLen <= 0) return "";
        // 恰好等于预算的行原样保留——TruncateByWidth 会为省略号预留 1 列，
        // 把不超宽行的最后一个字符误替换成 "…"。
        return TextWidthHelper.GetDisplayWidth(line) > maxLen
            ? TextWidthHelper.TruncateByWidth(line, maxLen)
            : line;
    }

    private static List<string> WordWrap(string text, int maxWidth)
    {
        return TextWidthHelper.WordWrapByWidth(text, maxWidth);
    }
}
