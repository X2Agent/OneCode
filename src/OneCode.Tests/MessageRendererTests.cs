using OneCode.App.Tui;

namespace OneCode.Tests;

public sealed class MessageRendererTests
{
    [Fact]
    public void BuildToolDetailLines_ExpandedResultRendersAllContent()
    {
        var result = string.Join("\n", Enumerable.Range(1, 30).Select(index => $"result-line-{index:D2}"));
        var tag = new ToolLineTag("Read", "{\"file_path\":\"sample.txt\"}", result, IsExpanded: true);

        var lines = MessageRenderer.BuildToolDetailLines(tag, viewportWidth: 80);
        var text = string.Join("\n", lines.Select(line => line.Text));

        text.Should().Contain("result-line-01");
        text.Should().Contain("result-line-30");
        text.Should().NotContain("more content available");
    }

    [Fact]
    public void BuildToolDetailLines_ExpandedArgumentsRenderAllWrappedSegments()
    {
        var longValue = new string('x', 240);
        var tag = new ToolLineTag("Bash", $"{{\"command\":\"{longValue}\"}}", null, IsExpanded: true);

        var lines = MessageRenderer.BuildToolDetailLines(tag, viewportWidth: 40);
        var renderedXCount = lines.Sum(line => line.Text.Count(ch => ch == 'x'));

        renderedXCount.Should().Be(240);
    }

    [Fact]
    public void BuildToolDetailLines_MixedResult_DecodesUnicodeEscapes()
    {
        var tag = new ToolLineTag(
            "Read",
            null,
            "状态：\\u8bfb\\u53d6\\u6210\\u529f",
            IsExpanded: true);

        var text = string.Join("\n", MessageRenderer.BuildToolDetailLines(tag, 80).Select(line => line.Text));

        text.Should().Contain("状态：读取成功");
        text.Should().NotContain("\\u8bfb");
    }

    [Fact]
    public void BuildToolDetailLines_WiderViewport_UsesFewerLinesWithoutLosingContent()
    {
        var content = string.Join("", Enumerable.Repeat("工具结果中文", 40));
        var tag = new ToolLineTag("Read", null, content, IsExpanded: true);

        var narrow = MessageRenderer.BuildToolDetailLines(tag, viewportWidth: 40);
        var wide = MessageRenderer.BuildToolDetailLines(tag, viewportWidth: 100);

        wide.Count.Should().BeLessThan(narrow.Count);
        string.Concat(narrow.Select(line => line.Text.Trim())).Should().Be(
            string.Concat(wide.Select(line => line.Text.Trim())));
    }

    [Fact]
    public void ReflowExpandedToolDetails_WidthChanges_RebuildsMaterializedDetailLines()
    {
        var content = string.Join("", Enumerable.Repeat("工具结果中文", 40));
        var tag = new ToolLineTag("Read", null, content, IsExpanded: true);
        var view = new MessageListView();
        view.AppendLines(new[]
        {
            FormattedLine.FromSegmentsWithTag(
                new[] { new LineSegment("▼ Read", TuiPalette.FgMuted) },
                tag),
        });

        view.ReflowExpandedToolDetails(40);
        var narrowLineCount = view.TotalLines;
        view.ReflowExpandedToolDetails(100);

        view.TotalLines.Should().BeLessThan(narrowLineCount);
        string.Concat(view.RenderedLines.Skip(1).Select(line => line.Trim())).Should().Be(content);
    }
}
