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
    public void FormatTarget_AskUserQuestion_ExtractsQuestionFromQuestionsArray()
    {
        const string input = "{\"questions\":[{\"id\":\"orm\",\"question\":\"请选择产品形态\",\"options\":[\"Web\",\"桌面端\"]}]}";

        var target = ToolResultSummarizer.FormatTarget("AskUserQuestion", input);

        target.Should().Contain("请选择产品形态");
        target.Should().NotContain("options");
    }

    [Fact]
    public void FormatTarget_AskUserQuestion_ExtractsWizardTitleInsteadOfRawJson()
    {
        const string input = "{\"title\":\"规划前确认\",\"questions\":[{\"id\":\"scope\",\"question\":\"目标模块？\"}]}";

        var target = ToolResultSummarizer.FormatTarget("AskUserQuestion", input);

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

    [Fact]
    public void FormatTarget_Agent_ShowsAgentNameAndDescription()
    {
        var target = ToolResultSummarizer.FormatTarget("Agent", """{"agent":"Explore","description":"调研近半月新闻","prompt":"..."}""");

        target.Should().Be("Explore · 调研近半月新闻");
    }

    [Fact]
    public void FormatTarget_Agent_LongDescriptionTruncatedTo60Chars()
    {
        var longDescription = new string('研', 80);
        var input = "{\"agent\":\"Explore\",\"description\":\"" + longDescription + "\"}";

        var target = ToolResultSummarizer.FormatTarget("Agent", input);

        target.Length.Should().Be(60);
        target.Should().EndWith("...");
    }

    [Fact]
    public void FormatTarget_Agent_MissingDescription_ShowsAgentNameOnly()
    {
        var target = ToolResultSummarizer.FormatTarget("Agent", """{"agent":"Explore"}""");

        target.Should().Be("Explore");
    }

    [Fact]
    public void FormatTarget_Agent_TruncatedJson_ShowsAgentNameViaTolerantExtract()
    {
        // ToolStart 摘要通道把 toolInput 截断到 100 字符——JSON 解析失败但
        // 容错提取仍能取到完整代理名，显示代理名而非整段 JSON。
        var truncated = """{"agent":"Explore","desc""" + new string('x', 200);

        var target = ToolResultSummarizer.FormatTarget("Agent", truncated);

        target.Should().Be("Explore");
    }

    [Fact]
    public void ExtractAgentNameTolerant_CompleteJson_ReturnsAgentName()
    {
        ToolResultSummarizer.ExtractAgentNameTolerant("""{"agent":"Explore","description":"调研","prompt":"..."}""")
            .Should().Be("Explore");
    }

    [Fact]
    public void ExtractAgentNameTolerant_TruncatedJsonWithCompleteValue_StillExtracts()
    {
        // 核心回归：ToolStart 通道传入截断 JSON，JsonDocument 解析失败，
        // 进度行注册必须仍能取到代理名，否则 SubagentProgress 静默丢失。
        var truncated = """"{"agent":"Explore","description":"调研近半月新闻","prompt":"继续"""";

        ToolResultSummarizer.ExtractAgentNameTolerant(truncated).Should().Be("Explore");
    }

    [Fact]
    public void ExtractAgentNameTolerant_TruncatedMidValue_ReturnsNull()
    {
        // 值本身被截断（无闭合引号）——返回 null 而非半截名字，
        // 半截名字与进度事件里的完整代理名对不上，注册了也不会被更新。
        ToolResultSummarizer.ExtractAgentNameTolerant("""{"agent":"Expl""").Should().BeNull();
    }

    [Fact]
    public void ExtractAgentNameTolerant_NonJsonInput_ReturnsNull()
    {
        ToolResultSummarizer.ExtractAgentNameTolerant("plain text input").Should().BeNull();
        ToolResultSummarizer.ExtractAgentNameTolerant(null).Should().BeNull();
        ToolResultSummarizer.ExtractAgentNameTolerant("").Should().BeNull();
    }

    [Fact]
    public void Summarize_WebFetchEnvelope_ShowsStatusAndSizeWithoutRepeatingUrl()
    {
        // 成功信封 {bytes, code, codeText, result, durationMs}：折叠行显示状态码 + 大小，
        // URL 由折叠行目标列展示，摘要里再重复一遍就是冗余。
        const string raw = """{"bytes":26513,"code":200,"codeText":"OK","result":"page body","durationMs":123}""";

        ToolResultSummarizer.Summarize("WebFetch", raw).Should().Be("200 OK · 25.9 KB");
    }

    [Fact]
    public void Summarize_WebFetchError_ReturnsFirstLineAsFailureReason()
    {
        // 错误结果经信封解包是错误消息纯文本——取首行作失败原因（如 403）。
        var raw = System.Text.Json.JsonSerializer.Serialize(new
        {
            Content = "403 Forbidden: access denied to https://example.com/paywall\nmore details",
            IsError = false,
        });

        ToolResultSummarizer.Summarize("WebFetch", raw).Should().Be("403 Forbidden: access denied to https://example.com/paywall");
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(26513, "25.9 KB")]
    [InlineData(3145728, "3 MB")]
    public void FormatBytes_RendersHumanReadableUnits(long byteCount, string expected)
    {
        ToolResultSummarizer.FormatBytes(byteCount).Should().Be(expected);
    }

    [Fact]
    public void Summarize_AgentSuccessEnvelope_ShowsAgentNameTurnsAndToolCalls()
    {
        // AgentTool 成功信封：完成行显示 "代理名 · 轮次 · 工具调用数"。
        const string raw = """{"agent":"Explore","conversationId":"abc","result":"findings...","turnsCompleted":3,"maxTurnsReached":false,"toolCalls":12}""";

        ToolResultSummarizer.Summarize("Agent", raw).Should().Be("Explore · 3 turns · 12 tool calls");
    }

    [Fact]
    public void Summarize_AgentErrorEnvelope_ShowsErrorTitle()
    {
        const string raw = """{"agent":"Explore","conversationId":"abc","result":null,"error":{"type":"tool_execution_failed","title":"Tool execution failed","detail":"boom"},"turnsCompleted":0,"maxTurnsReached":false}""";

        ToolResultSummarizer.Summarize("Agent", raw).Should().Be("Tool execution failed");
    }

    [Fact]
    public void Summarize_AgentBackgroundEnvelope_ShowsLaunchedInsteadOfResult()
    {
        // 后台模式信封无 result 文本（任务尚未完成）——摘要不得伪装成已完成。
        const string raw = """{"background":true,"taskId":"t-1","agent":"Explore","status":"started"}""";

        ToolResultSummarizer.Summarize("Agent", raw).Should().Be("launched in background");
    }

    [Fact]
    public void Summarize_AgentNonJsonResult_ReturnsNull()
        => ToolResultSummarizer.Summarize("Agent", "plain error text").Should().BeNull();
}
