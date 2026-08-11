using System.Text;

namespace OneCode.App.Tui;

/// <summary>
/// Mutable state for one streaming transcript lifecycle.
/// Keeps stream buffers, dynamic block ranges, and preview bookkeeping out of
/// <see cref="ChatTranscriptView"/> while leaving terminal rendering in the view.
/// </summary>
internal sealed class StreamingTranscriptState
{
    public StringBuilder TextBuffer { get; } = new();
    public StringBuilder ThinkingBuffer { get; } = new();
    public List<string> WrappedCompleteLines { get; } = [];
    public List<string> WrappedTextLines { get; } = [];
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
    public int ActivePlanCardLineIndex { get; set; } = -1;
    public int ActivePlanCardLineCount { get; set; }
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
        ActivePlanCardLineIndex = -1;
        ActivePlanCardLineCount = 0;
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
        WrappedCompleteLines.Clear();
        WrappedTextLines.Clear();
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
