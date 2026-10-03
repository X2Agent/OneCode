using System.Text.Json;
using OneCode.App.Tui;

namespace OneCode.Tests;

/// <summary>
/// 工具结果展开详情格式化守护：
/// 1. 双重编码信封（ToolResult 序列化 + MAF camelCase 包装）必须解包到工具原始输出；
/// 2. WebSearch 结构化结果渲染为"标题 / URL / 摘要"列表而非原始 JSON；
/// 3. 上游截断产生的非法 JSON 与纯文本结果不抛异常，回退可读展示。
/// </summary>
public sealed class ToolResultDetailFormatterTests
{
    [Fact]
    public void FormatDetail_WebSearchEnvelope_RendersResultList()
    {
        var inner = """
            {"provider":"tavily","query":"孙割 新闻","results":[
            {"title":"标题A","url":"https://a.example/x","snippet":"摘要A"},
            {"title":"标题B","url":"https://b.example/y","snippet":"摘要B"}],"durationSeconds":2.5}
            """;
        var raw = JsonSerializer.Serialize(new { Content = inner, IsError = false, Severity = "info" });

        var lines = ToolResultDetailFormatter.FormatDetail("WebSearch", raw);
        var combined = string.Join("", lines.Select(line => line.Text));

        lines[0].Text.Should().Be("搜索: 孙割 新闻 · tavily · 2 条");
        combined.Should().Contain("1. 标题A");
        combined.Should().Contain("2. 标题B");
        combined.Should().Contain("https://a.example/x");
        combined.Should().Contain("摘要A");
        combined.Should().NotContain("\\u", "信封解包后中文必须以原文展示");
        lines.Should().Contain(line => line.Text.Contains("https://a.example/x") && line.Fg == TuiPalette.Accent);
        lines.Should().Contain(line => line.Text.Contains("摘要A") && line.Fg == TuiPalette.FgSecondary);
    }

    [Fact]
    public void FormatDetail_TruncatedInvalidJson_FallsBackToRawText()
    {
        // 复现 2026-09 事故形态：上游把 camelCase 信封截断（500 字符 + "..."），
        // JSON 非法——格式化不得抛异常，回退原始文本逐行展示。
        var raw = """{"content":"{\"provider\":\"tavily\",\"query\":\"孙割 新闻\",\"results\":[{\"title\":\"标题A""";

        var lines = ToolResultDetailFormatter.FormatDetail("WebSearch", raw);
        var combined = string.Join("", lines.Select(line => line.Text));

        lines.Should().NotBeEmpty();
        combined.Should().Contain("tavily");
        combined.Should().Contain("标题A");
    }

    [Fact]
    public void FormatDetail_WebSearchMultilineJunkSnippet_CollapsesToSingleLine()
    {
        // Tavily 的 snippet 常夹带站点页脚样板（如 Reuters 的 Cookies/Privacy 页脚）与
        // 真实换行——必须折叠为单行再截断，不能在对话里多行铺开。
        var inner = """
            {"provider":"tavily","query":"q","results":[
            {"title":"标题A","url":"https://a.example/x","snippet":"正文第一行\nCookies, opens new tab\nTerms & Conditions\nPrivacy, opens new tab\nCopyright, opens new tab"}]}
            """;
        var raw = JsonSerializer.Serialize(new { Content = inner, IsError = false });

        var lines = ToolResultDetailFormatter.FormatDetail("WebSearch", raw);

        lines.Count(line => line.Text.Contains("Cookies")).Should().Be(1);
        lines.Count(line => line.Text.Contains("Terms & Conditions")).Should().Be(1);
        lines.Should().OnlyContain(line => !line.Text.EndsWith("\n") && !line.Text.EndsWith("\r"));
    }

    [Fact]
    public void FormatDetail_PlainTextResult_Unchanged()
    {
        var raw = """{"Content":"Total: 12 entries\n2025-01-01 12:00 a.txt","IsError":false,"Severity":"info"}""";

        var lines = ToolResultDetailFormatter.FormatDetail("LS", raw);

        lines[0].Text.Should().Be("Total: 12 entries");
        lines.Should().Contain(line => line.Text.Contains("a.txt"));
    }

    [Fact]
    public void FormatDetail_EmptyResult_ShowsPlaceholder()
    {
        var lines = ToolResultDetailFormatter.FormatDetail("Bash", "  ");

        lines.Should().ContainSingle().Which.Text.Should().Be("(空结果)");
    }

    [Fact]
    public void FormatDetail_WebSearchHtmlFooterSnippet_StripsTags()
    {
        // 历史转录的旧结果夹带站点页脚原始 HTML（<footer>/<p style=...>）——
        // 回放渲染前必须剥离标签，不得把标签噪音铺进对话。
        var inner = """
            {"provider":"tavily","query":"q","results":[
            {"title":"页脚","url":"https://a.example/x","snippet":"正文 <b>要点</b> 继续\n<footer><p style=\"margin-top:10px\">生成时间：2026-10-02</p></footer>"}]}
            """;
        var raw = JsonSerializer.Serialize(new { Content = inner, IsError = false });

        var lines = ToolResultDetailFormatter.FormatDetail("WebSearch", raw);

        var snippetLine = lines.Single(line => line.Text.Contains("正文"));
        snippetLine.Text.Should().NotContain("<b>");
        snippetLine.Text.Should().NotContain("<footer>");
        snippetLine.Text.Should().Contain("生成时间：2026-10-02", "标签剥掉但内容保留");
        lines.Should().NotContain(line => line.Text.Contains("<"));
    }
}
