using OneCode.App.Tui;
using OneCode.Infrastructure.Text;

namespace OneCode.Tests;

public sealed class TuiEventDispatchTests
{
    // UnifiedDiff.ComputeLineChanges

    [Fact]
    public void ComputeLineChanges_AddedLines_ReturnsAddedOnly()
    {
        var original = "line1\nline2\nline3";
        var modified = "line1\nline2\nline3\nline4\nline5";

        var (added, removed) = UnifiedDiff.ComputeLineChanges(original, modified);

        added.Should().Contain(new[] { "line4", "line5" });
        removed.Should().BeEmpty();
    }

    [Fact]
    public void ComputeLineChanges_RemovedLines_ReturnsRemovedOnly()
    {
        var original = "line1\nline2\nline3\nline4";
        var modified = "line1\nline2";

        var (added, removed) = UnifiedDiff.ComputeLineChanges(original, modified);

        added.Should().BeEmpty();
        removed.Should().Contain(new[] { "line3", "line4" });
    }

    [Fact]
    public void ComputeLineChanges_IdenticalContent_ReturnsEmpty()
    {
        var original = "line1\nline2";

        var (added, removed) = UnifiedDiff.ComputeLineChanges(original, original);

        added.Should().BeEmpty();
        removed.Should().BeEmpty();
    }

    [Fact]
    public void ComputeLineChanges_RespectsMaxLines()
    {
        var original = "old";
        var modified = string.Join("\n", Enumerable.Range(0, 50).Select(i => $"new{i}"));

        var (added, removed) = UnifiedDiff.ComputeLineChanges(original, modified, maxLines: 5);

        added.Length.Should().Be(5);
        removed.Should().ContainSingle().Which.Should().Be("old");
    }

    // ChatBlockRenderers.RenderDiffBlock

    [Fact]
    public void RenderDiffBlock_ProducesHeaderAndChanges()
    {
        var lines = ChatBlockRenderers.RenderDiffBlock(
            "Test.cs",
            new[] { "new line 1", "new line 2" },
            new[] { "old line 1" },
            addedSummary: 2,
            removedSummary: 1);

        lines.Should().NotBeEmpty();
        var fullText = string.Join("\n", lines.Select(l => l.FullText));
        fullText.Should().Contain("Test.cs");
        fullText.Should().Contain("+2");
        fullText.Should().Contain("-1");
        fullText.Should().Contain("+new line 1");
        fullText.Should().Contain("-old line 1");
    }

    // ChatBlockRenderers.RenderAgentCoordinationMessage

    [Fact]
    public void RenderAgentCoordinationMessage_ProducesFromToArrow()
    {
        var lines = ChatBlockRenderers.RenderAgentCoordinationMessage(
            "orchestrator", null, "researcher", null, "Investigate the issue");

        lines.Should().NotBeEmpty();
        var fullText = string.Join("\n", lines.Select(l => l.FullText));
        fullText.Should().Contain("orchestrator");
        fullText.Should().Contain("→");
        fullText.Should().Contain("researcher");
        fullText.Should().Contain("Investigate the issue");
    }

    // ChatBlockRenderers.RenderAgentMessage

    [Fact]
    public void RenderAgentMessage_ProducesAgentHeaderAndContent()
    {
        var lines = ChatBlockRenderers.RenderAgentMessage(
            "planner", null, "Here is my analysis of the architecture.");

        lines.Should().NotBeEmpty();
        var fullText = string.Join("\n", lines.Select(l => l.FullText));
        fullText.Should().Contain("Planner");
        fullText.Should().Contain("Here is my analysis of the architecture.");
    }
}
