namespace OneCode.App.Tui;

/// <summary>
/// Message renderer — design-spec chat content format:
///
///   [U] 帮我重构 StartupFlowCoordinator                    13:14
///
///   [O] orchestrator                                 13:14
///     ● 分析中...
///     当前耦合了 Terminal.Gui 创建逻辑...
///
///   [B] executor                                     13:14
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

    // message renderers

    private IReadOnlyList<FormattedLine> RenderUserMessage(UserMessage msg)
    {
        var time = msg.Timestamp.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        var lines = new List<FormattedLine>();
        var barColor = TuiPalette.GetModeBarColor(CurrentMode);
        var timeStr = $"{time}";
        const int scrollbarWidth = 1;
        var reservedRightWidth = scrollbarWidth + TuiSpacing.MessageTimestampRightPadding;
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
        var headerPad = Math.Max(
            1,
            CurrentWidth - reservedRightWidth - ContentIndent - content.Length - time.Length);

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
        var lines = new List<FormattedLine>();

        foreach (var block in msg.Content)
        {
            switch (block)
            {
                case TextBlock tb when !string.IsNullOrEmpty(tb.Text):
                    // Route assistant text through the Markdown renderer so
                    // code blocks, headings, lists, and tables render properly.
                    RenderMarkdownBlock(lines, tb.Text);
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
        var lines = new List<FormattedLine>();

        // Show tool name + status icon so the result is visually linked to its tool call.
        lines.Add(MakeToolLine(msg.ToolName, null, !msg.IsError));

        var displayContent = OneCode.Core.Tools.DisplayJsonSerializer.NormalizeForDisplay(msg.Content);
        var contentLines = displayContent.Replace("\r\n", "\n").Split('\n');
        var totalLines = contentLines.Length;

        if (totalLines <= MaxToolResultLines)
        {
            foreach (var line in contentLines)
                lines.Add(FormattedLine.Plain($"{Indent}  {TruncateLine(line)}", TuiPalette.FgSecondary));
        }
        else
        {
            var preview = Math.Min(3, MaxToolResultLines / 2);
            for (var i = 0; i < preview; i++)
                lines.Add(FormattedLine.Plain($"{Indent}  {TruncateLine(contentLines[i])}", TuiPalette.FgSecondary));
            lines.Add(FormattedLine.Plain(
                $"{Indent}  \u2026 ({totalLines - preview} more lines)",
                TuiPalette.FgMuted));
        }

        return lines;
    }

    private IReadOnlyList<FormattedLine> RenderSystemMessage(SystemMessage msg)
    {
        var lines = new List<FormattedLine>();
        var availableWidth = Math.Max(20, CurrentWidth - ContentIndent - 2);
        var isFirstLine = true;

        // StringReader.ReadLine() automatically handles \r\n, \n, and \r across platforms
        using var reader = new StringReader(msg.Content);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) != null)
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

    // Helpers

    /// <summary>Makes a tool-call inline row: ▸ Name  args   ✓/✗/◈
    /// Uses a right-pointing triangle (▸) — clickable to expand/collapse tool details.</summary>
    internal static FormattedLine MakeToolLine(string name, string? args, bool? ok, string? duration = null, string? result = null)
    {
        var segments = new List<LineSegment>
        {
            new($"{Indent}", TuiPalette.BgPrimary),
            new($"{TuiGlyphs.ToolCall} ", TuiPalette.Accent),
            new(name, TuiPalette.Warning),
        };
        if (!string.IsNullOrWhiteSpace(args))
            segments.Add(new($" \u00b7 {args}", TuiPalette.ToolDetailColor));
        if (ok is true)
        {
            var suffix = string.IsNullOrEmpty(duration) ? " \u00b7 完成" : $" \u00b7 {duration}";
            segments.Add(new(suffix, TuiPalette.Success));
        }
        else if (ok is false)
        {
            var suffix = string.IsNullOrEmpty(duration) ? " \u00b7 错误" : $" \u00b7 {duration}";
            segments.Add(new(suffix, TuiPalette.Error));
        }
        var tag = new ToolLineTag(name, args, result, IsExpanded: false);
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
        return TextWidthHelper.TruncateByWidth(line, maxLen);
    }

    private static List<string> WordWrap(string text, int maxWidth)
    {
        return TextWidthHelper.WordWrapByWidth(text, maxWidth);
    }
}
