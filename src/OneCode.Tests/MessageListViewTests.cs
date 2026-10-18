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

    [Fact]
    public void OnKeyDown_HomeAndEnd_Scrolls()
    {
        var view = new MessageListView();
        var lines = Enumerable.Range(1, 50).Select(i => Line($"line {i}")).ToList();
        view.AppendLines(lines);

        view.DispatchKeyDown(Terminal.Gui.Input.Key.Home);
        view.IsAutoScroll.Should().BeFalse();

        view.DispatchKeyDown(Terminal.Gui.Input.Key.End);
        view.IsAutoScroll.Should().BeTrue();
    }

    [Fact]
    public void ToggleLastDetails_ExpandsAndCollapsesMostRecentCollapsibleBlock()
    {
        var view = new MessageListView();
        var thinkingTag = new ThinkingLineTag("deep reasoning line 1\ndeep reasoning line 2", IsExpanded: false);
        view.AppendLines([
            Line("user prompt"),
            FormattedLine.FromSegmentsWithTag([new LineSegment("▸ Thought:", Terminal.Gui.Drawing.Color.Gray)], thinkingTag),
            Line("assistant reply"),
        ]);

        view.ToggleLastDetails().Should().BeTrue();
        view.RenderedLines.Should().Contain(l => l.Contains("deep reasoning line 1", StringComparison.Ordinal));

        view.ToggleLastDetails().Should().BeTrue();
        view.RenderedLines.Should().NotContain(l => l.Contains("deep reasoning line 1", StringComparison.Ordinal));
    }

    private static FormattedLine Line(string text) =>
        FormattedLine.Plain(text, Terminal.Gui.Drawing.Color.White);
}
