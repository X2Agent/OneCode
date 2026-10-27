using OneCode.WebUI.Services;

namespace OneCode.Tests;

/// <summary>
/// WebUI 对话正文渲染契约：
/// GFM 表格必须产出真实表格元素（管线漏了 UsePipeTables 会静默退化成段落）；
/// 原始 HTML 走白名单消毒（排版标签保留，脚本 / 事件属性 / 危险协议 / 表单控件剥离）；
/// 外链图片放行。
/// </summary>
public sealed class WebMarkdownRendererTests
{
    private readonly MarkdownRenderer _renderer = new();

    [Fact]
    public void ToHtml_PipeTable_RendersTableElement()
    {
        var html = _renderer.ToHtml("| 步骤 | 说明 |\n| --- | --- |\n| 扫描 | 检索调用点 |");

        html.Should().Contain("<table>");
        html.Should().Contain("<th>步骤</th>");
        html.Should().Contain("<td>检索调用点</td>");
    }

    [Fact]
    public void ToHtml_SafeInlineHtml_IsPreserved()
    {
        var html = _renderer.ToHtml("<span style=\"color:#F0564E\">重点</span>");

        html.Should().Contain("重点");
        html.Should().Contain("<span");
        html.Should().Contain("color");
    }

    [Fact]
    public void ToHtml_ScriptTag_IsRemoved()
    {
        var html = _renderer.ToHtml("<script>alert(1)</script>正文");

        html.Should().NotContain("<script");
        html.Should().NotContain("alert(1)");
        html.Should().Contain("正文");
    }

    [Fact]
    public void ToHtml_EventHandlerAttribute_IsStripped()
    {
        var html = _renderer.ToHtml("<div onclick=\"steal()\">点击</div>");

        html.Should().NotContain("onclick");
        html.Should().Contain("点击");
    }

    [Fact]
    public void ToHtml_JavascriptHref_IsStripped()
    {
        var html = _renderer.ToHtml("<a href=\"javascript:alert(1)\">链接</a>");

        html.Should().NotContain("javascript:");
        html.Should().Contain("链接");
    }

    [Fact]
    public void ToHtml_FormControl_IsRemoved()
    {
        var html = _renderer.ToHtml("<input value=\"x\" /><button>提交</button>");

        html.Should().NotContain("<input");
        html.Should().NotContain("<button");
    }

    [Fact]
    public void ToHtml_ExternalImage_IsPreserved()
    {
        var html = _renderer.ToHtml("![示意图](https://example.com/a.png)");

        html.Should().Contain("<img");
        html.Should().Contain("https://example.com/a.png");
    }
}
