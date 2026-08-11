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
}
