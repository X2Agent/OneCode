using System.Text.Json;
using OneCode.App.Tui;

namespace OneCode.Tests;

public sealed class ToolResultSummarizerTests
{
    [Fact]
    public void FormatTarget_UnknownTool_DecodesUnicodeEscapesForDisplay()
    {
        const string input = "{\"message\":\"\\u8bf7\\u9009\\u62e9\\u4ea7\\u54c1\"}";

        var target = ToolResultSummarizer.FormatTarget("CustomTool", input);

        target.Should().Contain("请选择产品");
        target.Should().NotContain("\\u8bf7");
    }

    [Fact]
    public void FormatTarget_AskUserQuestion_ExtractsQuestionInsteadOfRawJson()
    {
        const string input = "{\"question\":\"请选择产品形态\",\"options\":[\"Web\",\"桌面端\"]}";

        var target = ToolResultSummarizer.FormatTarget("AskUserQuestion", input);

        target.Should().Be("请选择产品形态");
        target.Should().NotContain("options");
    }

    [Fact]
    public void FormatTarget_AskUserQuestions_ExtractsWizardTitleInsteadOfRawJson()
    {
        const string input = "{\"title\":\"规划前确认\",\"questions\":[{\"id\":\"scope\",\"question\":\"目标模块？\"}]}";

        var target = ToolResultSummarizer.FormatTarget("AskUserQuestions", input);

        target.Should().Be("规划前确认");
        target.Should().NotContain("questions");
    }

    [Fact]
    public void Summarize_WebSearchRegisteredName_CountsStructuredResults()
    {
        // 注册名是 WebSearch——旧实现 case 写的 web_search 永不命中，折叠行没有摘要。
        var inner = """{"provider":"tavily","query":"q","results":[{},{},{}]}""";
        var raw = JsonSerializer.Serialize(new { Content = inner, IsError = false, Severity = "info" });

        ToolResultSummarizer.Summarize("WebSearch", raw).Should().Be("3 results");
    }

    [Fact]
    public void Summarize_WebSearchLegacySnakeName_AlsoMatches()
    {
        var inner = """{"results":[{}]}""";
        var raw = JsonSerializer.Serialize(new { Content = inner, IsError = false });

        ToolResultSummarizer.Summarize("web_search", raw).Should().Be("1 results");
    }

    [Fact]
    public void Summarize_Ls_ParsesTotalEntries()
    {
        var raw = JsonSerializer.Serialize(new { Content = "Total: 12 entries\n2025-01-01 12:00 a.txt", IsError = false });

        ToolResultSummarizer.Summarize("LS", raw).Should().Be("12 entries");
    }

    [Fact]
    public void FormatTarget_Ls_ExtractsPathInsteadOfRawJson()
    {
        ToolResultSummarizer.FormatTarget("LS", "{\"path\":\"src/OneCode.App\"}").Should().Be("src/OneCode.App");
    }

    [Fact]
    public void FormatTarget_WebSearch_ExtractsQueryInsteadOfRawJson()
    {
        ToolResultSummarizer.FormatTarget("WebSearch", "{\"query\":\"孙割 新闻\"}").Should().Be("孙割 新闻");
    }

    [Fact]
    public void FormatTarget_WebSearch_RawQueryFallback_WhenInputIsNotJson()
    {
        // ToolStart 摘要通道（ExtractToolInputSummary）传入裸 query 文本而非 JSON——
        // JSON 提取失败时回退裸值，与 ToolDone 通道（完整 JSON）显示一致。
        ToolResultSummarizer.FormatTarget("WebSearch", "孙割 9月25日 宣布").Should().Be("孙割 9月25日 宣布");
    }

    [Fact]
    public void FormatTarget_WebFetch_RawUrlFallback_WhenInputIsNotJson()
    {
        ToolResultSummarizer.FormatTarget("WebFetch", "https://example.com/post").Should().Be("https://example.com/post");
    }
}
