using OneCode.App.Tui;

namespace OneCode.Tests;

public sealed class MarkdownRendererTests
{
    [Fact]
    public void Render_PlainText_ReturnsAssistantLines()
    {
        var lines = MarkdownRenderer.Render("Hello world");

        lines.Should().NotBeEmpty();
        lines.Should().Contain(l => l.Text.Contains("Hello world"));
    }

    [Fact]
    public void Render_Heading_ReturnsSystemLine()
    {
        var lines = MarkdownRenderer.Render("# Title");

        lines.Should().Contain(l => l.Role == LineRole.System && l.Text.Contains("Title"));
    }

    [Fact]
    public void Render_FencedCodeBlock_ReturnsCodeWithBorder()
    {
        var md = "```csharp\nvar x = 1;\n```";
        var lines = MarkdownRenderer.Render(md);

        lines.Should().Contain(l => l.Text.Contains("csharp"));
        lines.Should().Contain(l => l.Text.Contains("var x = 1;"));
        lines.Should().Contain(l => l.Text.Contains("┌─"));
        lines.Should().Contain(l => l.Text.Contains("└─"));
    }

    // 代码块头部不再渲染 [ copy ] 按钮——窄面板中它挤占代码列宽，且点击复制
    // 在侧边栏场景不可用。回归防护：确保 copy 标签彻底移除。
    [Fact]
    public void Render_FencedCodeBlock_HasNoCopyButton()
    {
        var md = "```csharp\nvar x = 1;\n```";
        var lines = MarkdownRenderer.Render(md);

        lines.Should().NotContain(l => l.Text.Contains("copy"));
        lines.Should().NotContain(l => l.Tag != null,
            "代码块头部不再挂载点击交互标签");
    }

    // 窄面板（<60 列）中表格改用记录式渲染：每行一条记录、列名前缀、值换行不截断
    //（回归场景：42-50 列侧边栏中网格表格被等比压缩，每列只剩省略号）
    [Fact]
    public void Render_Table_NarrowWidth_RendersAsRecordsWithoutTruncation()
    {
        var md = "| 步骤 | 说明 |\n| --- | --- |\n| 扫描 | 使用 Grep 检索调用点 |\n| 改写 | 更新渲染管线 |";
        var lines = MarkdownRenderer.Render(md, viewWidth: 48);
        var text = string.Join("\n", lines.Select(l => l.Text));

        text.Should().Contain("步骤 │ 扫描", "记录式渲染：列名前缀 + 单元格值");
        text.Should().Contain("使用 Grep 检索调用点");
        text.Should().NotContain("…",
            "记录式渲染按宽度换行而非截断，不出现省略号");
        text.Should().NotContain("│ 扫描 │",
            "窄屏不再渲染网格行");
    }

    // 宽屏（对话流）保持网格表格渲染
    [Fact]
    public void Render_Table_WideWidth_RendersGridRow()
    {
        var md = "| 步骤 | 说明 |\n| --- | --- |\n| 扫描 | 检索调用点 |";
        var lines = MarkdownRenderer.Render(md, viewWidth: 100);

        lines.Should().Contain(l => l.Text.Contains("│ 扫描"),
            "宽屏保持网格表格");
    }

    [Fact]
    public void Render_CodeBlockWithoutLang_ReturnsCodeWithBorder()
    {
        var md = "```\nhello\n```";
        var lines = MarkdownRenderer.Render(md);

        lines.Should().Contain(l => l.Text.Contains("hello"));
        lines.Should().Contain(l => l.Text.Contains("┌─"));
    }

    [Fact]
    public void Render_DiffAddedLine_ReturnsDiffAddedRole()
    {
        var md = "```diff\n+added line\n-removed line\n```";
        var lines = MarkdownRenderer.Render(md);

        lines.Should().Contain(l => l.Role == LineRole.DiffAdded && l.Text.Contains("+added line"));
        lines.Should().Contain(l => l.Role == LineRole.DiffRemoved && l.Text.Contains("-removed line"));
    }

    [Fact]
    public void Render_BlockQuote_ReturnsWithBorderPrefix()
    {
        var md = "> This is a quote";
        var lines = MarkdownRenderer.Render(md);

        lines.Should().Contain(l => l.Text.Contains("│") && l.Text.Contains("This is a quote"));
    }

    [Fact]
    public void Render_UnorderedList_ReturnsWithBullet()
    {
        var md = "- item1\n- item2";
        var lines = MarkdownRenderer.Render(md);

        lines.Should().Contain(l => l.Text.Contains("•") && l.Text.Contains("item1"));
        lines.Should().Contain(l => l.Text.Contains("•") && l.Text.Contains("item2"));
    }

    [Fact]
    public void Render_OrderedList_ReturnsWithNumber()
    {
        var md = "1. first\n2. second";
        var lines = MarkdownRenderer.Render(md);

        lines.Should().Contain(l => l.Text.Contains("1.") && l.Text.Contains("first"));
        lines.Should().Contain(l => l.Text.Contains("2.") && l.Text.Contains("second"));
    }

    [Fact]
    public void Render_ThematicBreak_ReturnsHorizontalRule()
    {
        var md = "above\n\n---\n\nbelow";
        var lines = MarkdownRenderer.Render(md);

        lines.Should().Contain(l => l.Text.Contains("────────"));
    }

    [Fact]
    public void Render_InlineCode_RendersContent()
    {
        var md = "Use `var x = 1;` here";
        var lines = MarkdownRenderer.Render(md);

        lines.Should().Contain(l => l.Text.Contains("var x = 1;"));
    }

    [Fact]
    public void Render_BoldAndItalic_RendersText()
    {
        var md = "This is **bold** and *italic* text";
        var lines = MarkdownRenderer.Render(md);

        lines.Should().Contain(l => l.Text.Contains("bold"));
        lines.Should().Contain(l => l.Text.Contains("italic"));
        // Must also verify markdown markers are stripped — otherwise
        // "**bold**" also contains "bold" and the test passes even if
        // the renderer fails to parse inline formatting.
        lines.Should().NotContain(l => l.Text.Contains("**"));
        lines.Should().NotContain(l => l.Text.Contains("*italic*"));
    }

    [Fact]
    public void Render_EmptyInput_ReturnsBlankLine()
    {
        var lines = MarkdownRenderer.Render("");

        lines.Should().NotBeEmpty();
    }

    [Fact]
    public void Render_NullInput_ReturnsBlankLine()
    {
        var lines = MarkdownRenderer.Render(null!);

        lines.Should().NotBeEmpty();
    }

    [Fact]
    public void Render_NestedList_ReturnsIndented()
    {
        var md = "- outer\n  - inner";
        var lines = MarkdownRenderer.Render(md);

        lines.Should().Contain(l => l.Text.Contains("outer"));
        lines.Should().Contain(l => l.Text.Contains("inner"));
    }

    [Fact]
    public void Render_DiffHunkHeader_ReturnsDiffHunkRole()
    {
        var md = "```diff\n@@ -1,3 +1,4 @@\n+new line\n```";
        var lines = MarkdownRenderer.Render(md);

        lines.Should().Contain(l => l.Role == LineRole.DiffHunk);
    }
}
