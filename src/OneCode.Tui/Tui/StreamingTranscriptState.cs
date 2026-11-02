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

    /// <summary>
    /// 已闭合 markdown 块的渲染结果。流式预览与最终提交共用同一个渲染器，
    /// 因此用户看到的形态在流结束瞬间不再跳变。
    /// </summary>
    public List<FormattedLine> CommittedBlocks { get; } = [];

    /// <summary>已渲染进 <see cref="CommittedBlocks"/> 的源偏移（下一个待切块起点）。</summary>
    public int CommittedBufferEnd { get; set; }

    /// <summary>生成 <see cref="CommittedBlocks"/> 时的宽度：宽度变化即整体失效。</summary>
    public int BlocksWidth { get; set; }

    public List<FormattedLine> StatusLines { get; } = [];
    public StreamingToolTracker ToolTracker { get; } = new();

    public AssistantMessage? Assistant { get; set; }
    public List<FormattedLine>? PendingLines { get; set; }
    public bool IsStreaming { get; set; }
    public bool HasThinking { get; set; }
    public bool HasReplyStarted { get; set; }
    public int TrailingModeBannerLineCount { get; set; }
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
        ResetTurnState(clearSeenTools: true);
    }

    public void ResetForNextTurn()
    {
        ResetTurnState(clearSeenTools: false);
        IsStreaming = true;
    }

    public void CompleteStream()
    {
        IsStreaming = false;
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
        CommittedBlocks.Clear();
        CommittedBufferEnd = 0;
        BlocksWidth = 0;
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
