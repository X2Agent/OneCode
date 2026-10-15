using OneCode.App.Tui;

namespace OneCode.Tests;

/// <summary>
/// 工具结果展开详情的按工具格式化：WebFetch 专用（头行 = 状态 · 大小 · prompt，
/// 正文预览有行数上限），其余工具走通用上限路径。
/// </summary>
public sealed class ToolResultDetailFormatterTests
{
    private static string WebFetchEnvelope(string result, long bytes = 26513, int code = 200, string codeText = "OK")
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            bytes,
            code,
            codeText,
            result,
            durationMs = 123,
        });

    [Fact]
    public void FormatDetail_WebFetchEnvelope_HeaderShowsStatusSizeAndPrompt()
    {
        // 正文首行形如 "Content from URL (prompt: 总结此页):"，prompt 提取进头行，
        // 正文跳过该行——否则 prompt 与正文重复出现两遍。
        var result = "Content from URL (prompt: 总结此页):\n正文第一行\n正文第二行\n";

        var lines = ToolResultDetailFormatter.FormatDetail("WebFetch", WebFetchEnvelope(result));

        lines[0].Text.Should().Contain("200 OK").And.Contain("25.9 KB").And.Contain("总结此页");
        lines.Should().NotContain(line => line.Text.StartsWith("Content from URL"),
            "prompt 首行已并入头行，不得重复出现");
        lines.Select(line => line.Text).Should().Contain("正文第一行").And.Contain("正文第二行");
    }

    [Fact]
    public void FormatDetail_WebFetchEnvelope_LongBody_TruncatesWithRemainingLineCount()
    {
        // 完整正文已发送给模型，对话流里只做概览：正文超过 30 行折叠为剩余行数提示。
        var body = string.Join("\n", Enumerable.Range(1, 50).Select(i => $"line-{i}"));
        var result = $"Content from URL (prompt: p):\n{body}\n";

        var lines = ToolResultDetailFormatter.FormatDetail("WebFetch", WebFetchEnvelope(result));

        // 头行 + 30 行预览 + 1 行剩余提示
        lines.Should().HaveCount(32);
        lines[^1].Text.Should().Be("… 还有 20 行（完整内容已发送给模型）");
        lines.Select(line => line.Text).Should().Contain("line-30");
        lines.Select(line => line.Text).Should().NotContain("line-31");
    }

    [Fact]
    public void FormatDetail_WebFetchEnvelope_EmptyBody_ShowsPlaceholder()
    {
        var result = "Content from URL (prompt: p):\n";

        var lines = ToolResultDetailFormatter.FormatDetail("WebFetch", WebFetchEnvelope(result));

        lines.Should().Contain(line => line.Text == "(空正文)");
    }

    [Fact]
    public void FormatDetail_WebFetchPlainTextError_FallsBackToGeneric()
    {
        // 错误结果不是成功信封 JSON——回退通用展示，把错误消息铺出来。
        var lines = ToolResultDetailFormatter.FormatDetail("WebFetch", "403 Forbidden: blocked");

        lines.Should().Contain(line => line.Text.Contains("403 Forbidden"));
    }

    [Fact]
    public void FormatDetail_Generic_LongOutput_TruncatesAtSixtyLinesWithHint()
    {
        var content = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"row-{i}"));

        var lines = ToolResultDetailFormatter.FormatDetail("CustomTool", content);

        // 60 行预览 + 1 行剩余提示
        lines.Should().HaveCount(61);
        lines[^1].Text.Should().Contain("还有更多行（完整结果已发送给模型）");
        lines.Select(line => line.Text).Should().Contain("row-60");
        lines.Select(line => line.Text).Should().NotContain("row-61");
    }

    [Fact]
    public void FormatDetail_AgentEnvelope_ShowsMetaHeaderThenResultText()
    {
        // AgentTool 成功信封：详情展开 = 元信息头行（turns · tool calls）+ result 正文（非原始 JSON）。
        const string raw = """{"agent":"Explore","conversationId":"abc","result":"line one\nline two","turnsCompleted":3,"maxTurnsReached":false,"toolCalls":12}""";

        var lines = ToolResultDetailFormatter.FormatDetail("Agent", raw);

        lines[0].Text.Should().Be("3 turns · 12 tool calls");
        lines.Select(line => line.Text).Should().Contain("line one");
        lines.Select(line => line.Text).Should().Contain("line two");
        lines.Select(line => line.Text).Should().NotContain(raw, "原始 JSON 不得直接铺给用户");
    }

    [Fact]
    public void FormatDetail_AgentEnvelope_MaxTurnsReached_IsFlaggedInHeader()
    {
        const string raw = """{"agent":"Explore","conversationId":"abc","result":"partial","turnsCompleted":50,"maxTurnsReached":true,"toolCalls":49}""";

        var lines = ToolResultDetailFormatter.FormatDetail("Agent", raw);

        lines[0].Text.Should().Contain("50 turns");
        lines[0].Text.Should().Contain("达到上限");
    }

    [Fact]
    public void FormatDetail_AgentBackgroundEnvelope_FallsBackToGeneric()
    {
        // 后台模式信封无 result 文本——回退通用展示（不渲染发言/正文块）。
        const string raw = """{"background":true,"taskId":"t-1","agent":"Explore","status":"started"}""";

        var lines = ToolResultDetailFormatter.FormatDetail("Agent", raw);

        lines.Should().Contain(line => line.Text.Contains("background"), "generic 视图应显示原始信封内容");
    }
}
