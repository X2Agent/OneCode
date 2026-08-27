using OneCode.App.Tui;

namespace OneCode.Tests;

/// <summary>
/// 工具调用行宽度自适应守护（视口裁切回归）：
/// 1. <see cref="ConversationRenderer.MakeCompletedToolLine"/> / <see cref="MessageFlowRenderer.MakeToolLine"/>
///    在 maxWidth &gt; 0 时整行显示宽度不得超过预算（尾部预留滚动条列）；
/// 2. 超长 args/摘要按显示宽度截断（CJK 双宽感知），以省略号结尾而非被终端裁切；
/// 3. maxWidth = 0 保持旧行为（不截断，供测试/特殊场景使用）。
/// </summary>
public sealed class ToolLineWidthTests
{
    [Fact]
    public void CompletedToolLine_LongTarget_TruncatesToMaxWidth()
    {
        // Bash 命令目标被 FormatTarget 截到 50 字符；CJK 下 50 字符 = 100 列，
        // 必须再按显示宽度截断才能装进 80 列视口。
        var cjkCommand = string.Concat(Enumerable.Repeat("编译整个解决方案", 30));
        var line = ConversationRenderer.MakeCompletedToolLine(
            "Bash", false, $$"""{"command":"{{cjkCommand}}"}""", "(1ms)", maxWidth: 80);

        TextWidthHelper.GetDisplayWidth(line.FullText).Should().BeLessThanOrEqualTo(79);
        line.FullText.Should().Contain("…", "超长目标应截断而非被视口裁切");
    }

    [Fact]
    public void CompletedToolLine_CjkArgs_TruncatesByDisplayWidthNotCharCount()
    {
        // 未知工具走 JSON 展示分支（截到 40 字符）；CJK 40 字符 = 80 列，
        // 加上工具名等前缀必然超出 60 列预算——按显示宽度截断后必须装得下。
        var cjkJson = $$"""{"question":"{{string.Concat(Enumerable.Repeat("请选择", 30))}}"}""";
        var line = ConversationRenderer.MakeCompletedToolLine(
            "AskUserQuestion", false, cjkJson, null, maxWidth: 60);

        TextWidthHelper.GetDisplayWidth(line.FullText).Should().BeLessThanOrEqualTo(59);
    }

    [Fact]
    public void CompletedToolLine_ShortContent_NotTruncated()
    {
        var line = ConversationRenderer.MakeCompletedToolLine(
            "Read", false, """{"path":"src/a.cs"}""", "(3ms)", result: "ok", maxWidth: 120);

        line.FullText.Should().NotContain("…");
        line.FullText.Should().Contain("src/a.cs");
    }

    [Fact]
    public void CompletedToolLine_ZeroMaxWidth_KeepsLegacyUnboundedBehavior()
    {
        var cjkCommand = string.Concat(Enumerable.Repeat("编译整个解决方案", 30));
        var line = ConversationRenderer.MakeCompletedToolLine(
            "Bash", false, $$"""{"command":"{{cjkCommand}}"}""", null, maxWidth: 0);

        line.FullText.Should().NotContain("…");
    }

    [Fact]
    public void StreamingToolLine_LongArgs_TruncatesToMaxWidth()
    {
        var cjkCommand = string.Concat(Enumerable.Repeat("编译整个解决方案", 30));
        var line = MessageFlowRenderer.MakeToolLine(
            "Bash", cjkCommand, ok: null, maxWidth: 70);

        TextWidthHelper.GetDisplayWidth(line.FullText).Should().BeLessThanOrEqualTo(69);
        line.FullText.Should().Contain("…");
    }

    [Fact]
    public void StreamingToolLine_ZeroMaxWidth_KeepsLegacyUnboundedBehavior()
    {
        var line = MessageFlowRenderer.MakeToolLine(
            "Bash", string.Concat(Enumerable.Repeat("编译", 100)), ok: null, maxWidth: 0);

        line.FullText.Should().NotContain("…");
    }
}
