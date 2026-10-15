using OneCode.App.Tui;
using OneCode.Core.Domain;

namespace OneCode.Tests;

public sealed class MessageFlowRendererTests
{
    [Fact]
    public void RenderUserMessage_HeaderRightAlignsTimestampWithScrollbarGap()
    {
        const int width = 80;
        var renderer = new MessageFlowRenderer { CurrentWidth = width };
        var message = new UserMessage(
            "user-message",
            "hello",
            new DateTimeOffset(2026, 7, 28, 12, 30, 0, TimeSpan.Zero));
        var expectedTime = message.Timestamp.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

        var rendered = renderer.RenderMessage(message);
        rendered.Should().ContainSingle();
        var header = rendered[0].FullText;

        header.Should().HaveLength(width - 3);
        header.Should().Contain("hello");
        header.Should().EndWith(expectedTime);
        header.Should().NotContain(Environment.UserName);
    }

    [Fact]
    public void RenderToolResultMessage_MixedText_DecodesUnicodeEscapes()
    {
        var renderer = new MessageFlowRenderer { CurrentWidth = 80 };
        var message = new ToolResultMessage(
            "tool-result",
            "call-1",
            "Read",
            "状态：\\u8bfb\\u53d6\\u6210\\u529f",
            false,
            DateTimeOffset.UtcNow);

        var text = string.Join("\n", renderer.RenderMessage(message).Select(line => line.FullText));

        text.Should().Contain("状态：读取成功");
        text.Should().NotContain("\\u8bfb");
    }

    [Fact]
    public void TrimReplyLeadingWhitespace_LeadingSpacesAndTabs_AreRemoved()
    {
        // 部分模型在 reasoning/text 交错的流式输出里给文本段带上大段前导缩进，
        // 原样渲染会让整段看起来居中——回复开头的前导空白必须剥掉。
        MessageFlowRenderer.TrimReplyLeadingWhitespace("                    再补两个检索")
            .Should().Be("再补两个检索");
        MessageFlowRenderer.TrimReplyLeadingWhitespace("\t\t标题")
            .Should().Be("标题");
    }

    [Fact]
    public void TrimReplyLeadingWhitespace_AllWhitespace_BecomesEmpty()
    {
        // 剥完为空的段落（纯前导空白）由调用方直接丢弃，不产生空行。
        MessageFlowRenderer.TrimReplyLeadingWhitespace("    \t  ").Should().BeEmpty();
    }

    [Fact]
    public void TrimReplyLeadingWhitespace_InteriorIndentation_IsPreserved()
    {
        // 只处理回复开头：正文与代码块内部的缩进有语义，不动。
        const string text = "  def foo():\n      return 1";

        MessageFlowRenderer.TrimReplyLeadingWhitespace(text)
            .Should().Be("def foo():\n      return 1");
    }
}
