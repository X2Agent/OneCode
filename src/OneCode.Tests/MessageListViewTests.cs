using OneCode.App.Tui;

namespace OneCode.Tests;

/// <summary>
/// Tail-region lifecycle in <see cref="MessageListView"/>: the plan-approval
/// InlineSelector is appended while the agent run is finalizing — before
/// EndStreaming commits the streaming preview. The preview commit extends to
/// the list end and must not swallow the appended selector lines.
/// </summary>
public sealed class MessageListViewTests
{
    [Fact]
    public void ReplaceStreamingPreview_WithActiveTailRegion_KeepsTailAfterCommittedLines()
    {
        var view = new MessageListView();
        view.AppendLines([Line("history line")]);
        view.BeginStreamingPreview();
        view.ReplaceStreamingPreview([Line("streaming preview")]);

        // Plan approval selector pops mid-finalize: tail region lands after the preview.
        view.BeginTailRegion([Line("▸ 批准并执行"), Line("  拒绝计划")]);

        // EndStreaming commits the final markdown — the preview window is replaced.
        view.ReplaceStreamingPreview([Line("final committed text")]);

        view.RenderedLines.Should().Equal(
            "history line", "final committed text", "▸ 批准并执行", "  拒绝计划");
    }

    [Fact]
    public void ReplaceStreamingPreview_WithoutTailRegion_ReplacesPreviewOnly()
    {
        var view = new MessageListView();
        view.AppendLines([Line("history line")]);
        view.BeginStreamingPreview();
        view.ReplaceStreamingPreview([Line("streaming preview")]);
        view.ReplaceStreamingPreview([Line("final committed text")]);

        view.RenderedLines.Should().Equal("history line", "final committed text");
    }

    [Fact]
    public void ReplaceTailRegion_AfterPreviewCommit_StillTargetsSelectorLines()
    {
        var view = new MessageListView();
        view.AppendLines([Line("history line")]);
        view.BeginStreamingPreview();
        view.ReplaceStreamingPreview([Line("streaming preview")]);
        view.BeginTailRegion([Line("option A")]);

        view.ReplaceStreamingPreview([Line("final committed text")]);
        view.ReplaceTailRegion([Line("option B")]);

        view.RenderedLines.Should().Equal("history line", "final committed text", "option B");
    }

    private static FormattedLine Line(string text) =>
        FormattedLine.Plain(text, Terminal.Gui.Drawing.Color.White);
}
