using System.Text;

namespace OneCode.Tui;

/// <summary>
/// Mutable state for one streaming transcript lifecycle.
/// Keeps stream buffers, dynamic block ranges, and preview bookkeeping out of
/// <see cref="ChatTranscriptView"/> while leaving terminal rendering in the view.
/// </summary>
internal sealed class StreamingTranscriptState
{
    public StringBuilder TextBuffer { get; } = new();
    public StringBuilder ThinkingBuffer { get; } = new();
    public List<string> WrappedTextLines { get; } = [];

    /// <summary>
    /// 与 <see cref="WrappedTextLines"/> 一一对应的渲染行：稳定前缀跨帧复用同一批
    /// <see cref="FormattedLine"/> 实例，只有尾段随 token 重算，避免每帧重物化整段回复。
    /// </summary>
    public List<FormattedLine> WrappedFormattedLines { get; } = [];

    /// <summary><see cref="WrappedTextLines"/> 前缀中已完成段落占用的行数：这些行跨帧稳定，只追加不重算。</summary>
    public int WrappedStableCount { get; set; }
    public List<FormattedLine> StatusLines { get; } = [];
    public StreamingToolTracker ToolTracker { get; } = new();

    public AssistantMessage? Assistant { get; set; }
    public List<FormattedLine>? PendingLines { get; set; }
    public bool IsStreaming { get; set; }
    public bool HasThinking { get; set; }
    public bool HasReplyStarted { get; set; }
    public int PreviewLineCount { get; set; }
    public int TrailingModeBannerLineCount { get; set; }
    public int WrappedCompletedEnd { get; set; }
    public int WrappedBufferLength { get; set; }
    public int WrappedWidth { get; set; }
    public int BuildRunStatusLineIndex { get; set; } = -1;
    public int BuildRunStatusLineCount { get; set; }
    public int ModeProgressLineIndex { get; set; } = -1;
    public int ModeProgressLineCount { get; set; }
    public int ThinkingSummaryLineIndex { get; set; } = -1;
    public int ThinkingBlockLineCount { get; set; }
    public long ThinkingStartTick { get; set; }
    public object? RebuildTimer { get; set; }
    public bool RebuildPending { get; set; }

    public void ResetForNewStream(AssistantMessage assistant)
    {
        IsStreaming = true;
        Assistant = assistant;
        PendingLines = [];
        PreviewLineCount = 0;
        ResetTurnState(clearSeenTools: true);
    }

    public void ResetForNextTurn()
    {
        PreviewLineCount = 0;
        ResetTurnState(clearSeenTools: false);
        IsStreaming = true;
    }

    public void CompleteStream()
    {
        IsStreaming = false;
        PreviewLineCount = 0;
        PendingLines = null;
        StatusLines.Clear();
        BuildRunStatusLineIndex = -1;
        BuildRunStatusLineCount = 0;
        ModeProgressLineIndex = -1;
        ModeProgressLineCount = 0;
        ToolTracker.Clear();
        HasThinking = false;
        HasReplyStarted = false;
        ThinkingSummaryLineIndex = -1;
        ThinkingBlockLineCount = 0;
        ThinkingBuffer.Clear();
        Assistant = null;
        TextBuffer.Clear();
        TrailingModeBannerLineCount = 0;
    }

    public void Clear()
    {
        IsStreaming = false;
        Assistant = null;
        PendingLines = null;
        PreviewLineCount = 0;
        TrailingModeBannerLineCount = 0;
        RebuildTimer = null;
        RebuildPending = false;
        ResetTurnState(clearSeenTools: true);
    }

    private void ResetTurnState(bool clearSeenTools)
    {
        TextBuffer.Clear();
        StatusLines.Clear();
        BuildRunStatusLineIndex = -1;
        BuildRunStatusLineCount = 0;
        ModeProgressLineIndex = -1;
        ModeProgressLineCount = 0;
        WrappedTextLines.Clear();
        WrappedFormattedLines.Clear();
        WrappedStableCount = 0;
        WrappedCompletedEnd = 0;
        WrappedBufferLength = 0;
        WrappedWidth = 0;
        HasThinking = false;
        HasReplyStarted = false;
        ThinkingSummaryLineIndex = -1;
        ThinkingBlockLineCount = 0;
        ThinkingBuffer.Clear();
        ThinkingStartTick = 0;
        if (clearSeenTools)
            ToolTracker.Clear();
        else
            ToolTracker.ClearPending();
    }
}
