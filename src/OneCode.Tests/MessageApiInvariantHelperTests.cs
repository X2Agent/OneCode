using Microsoft.Extensions.AI;
using OneCode.App.Services.Compact;

namespace OneCode.Tests;

/// <summary>
/// 消息 API 不变量的传输侧契约：同轮存在工具调用/结果内容时，
/// 空文本内容必须在传输前剥离（部分 API 对空 text 段报 400）。
/// </summary>
public sealed class MessageApiInvariantHelperTests
{
    [Fact]
    public void NormalizeForToolCallingTransport_StripsEmptyText_WhenToolContentsExist()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.Assistant, new AIContent[]
            {
                new TextContent(string.Empty),
                new FunctionCallContent("call_1", "LS", null)
            }),
            new ChatMessage(ChatRole.User, new AIContent[]
            {
                new TextContent(string.Empty),
                new FunctionResultContent("call_1", "ok")
            })
        };

        var normalized = MessageApiInvariantHelper.NormalizeForToolCallingTransport(messages);

        foreach (var message in normalized)
        {
            message.Contents.Should().NotContain(
                c => c is TextContent && ((TextContent)c).Text == string.Empty);
        }
        normalized[0].Contents.Should().Contain(c => c is FunctionCallContent);
        normalized[1].Contents.Should().Contain(c => c is FunctionResultContent);
    }
}
