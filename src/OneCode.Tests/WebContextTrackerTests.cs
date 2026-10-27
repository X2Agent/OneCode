using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Query;
using OneCode.WebUI.Services;

namespace OneCode.Tests;

/// <summary>
/// WebContextTracker——上下文清单提取规则：
/// 文件读写与网页访问必须进清单，无关工具与非 JSON 入参必须被忽略且不抛。
/// </summary>
public sealed class WebContextTrackerTests
{
    private static readonly NullLogger Logger = NullLogger.Instance;

    [Fact]
    public void AccumulateToolUse_ReadTool_AddsFileWithReadCount()
    {
        List<WebContextItem> context = [];

        WebContextTracker.AccumulateToolUse(context, "Read", """{"filePath":"src/a.cs"}""", Logger);

        var item = context.Should().ContainSingle().Which;
        item.Kind.Should().Be(WebContextKind.File);
        item.Target.Should().Be("src/a.cs");
        item.ReadCount.Should().Be(1);
        item.WriteCount.Should().Be(0);
    }

    [Fact]
    public void AccumulateToolUse_WriteTool_SameFileAccumulatesWriteCount()
    {
        List<WebContextItem> context = [];

        WebContextTracker.AccumulateToolUse(context, "Write", """{"filePath":"src/a.cs"}""", Logger);
        WebContextTracker.AccumulateToolUse(context, "Edit", """{"filePath":"src/a.cs"}""", Logger);

        var item = context.Should().ContainSingle().Which;
        item.WriteCount.Should().Be(2);
        item.ReadCount.Should().Be(0);
    }

    [Fact]
    public void AccumulateToolUse_WebFetch_AddsLink()
    {
        List<WebContextItem> context = [];

        WebContextTracker.AccumulateToolUse(context, "WebFetch", """{"url":"https://example.com/post"}""", Logger);

        var item = context.Should().ContainSingle().Which;
        item.Kind.Should().Be(WebContextKind.Link);
        item.Target.Should().Be("https://example.com/post");
    }

    [Fact]
    public void AccumulateToolUse_NonFileTool_IsIgnored()
    {
        List<WebContextItem> context = [];

        WebContextTracker.AccumulateToolUse(context, "Bash", """{"command":"ls"}""", Logger);

        context.Should().BeEmpty();
    }

    [Fact]
    public void AccumulateToolUse_NonJsonInput_DoesNotThrowAndIsIgnored()
    {
        List<WebContextItem> context = [];

        var act = () => WebContextTracker.AccumulateToolUse(context, "Read", "src/a.cs", Logger);

        act.Should().NotThrow();
        context.Should().BeEmpty();
    }

    [Fact]
    public void AccumulateFileChange_OrderAddsLineCountsForSameFile()
    {
        List<WebContextItem> context = [];

        WebContextTracker.AccumulateFileChange(
            context, new FileChangeEvent("src/a.cs", ["+1", "+2"], ["-1"]));
        WebContextTracker.AccumulateFileChange(
            context, new FileChangeEvent("src/a.cs", ["+3"], []));

        var item = context.Should().ContainSingle().Which;
        item.AddedLines.Should().Be(3);
        item.RemovedLines.Should().Be(1);
    }
}
