using Microsoft.Extensions.AI;
using OneCode.App.Session;

namespace OneCode.Tests;

/// <summary>
/// 跨轮回放的 wire 协议守卫：工具结果必须映射为 <see cref="ChatRole.Tool"/>。
/// OpenAI 适配器对 User 角色消息里的 <see cref="FunctionResultContent"/> 静默丢弃，
/// 按 User 角色回放会让恢复后的会话出现"assistant 带 tool_calls 但没有任何 tool
/// 结果"的畸形历史（2026-09 静默空响应事故的诱因之一）。
/// </summary>
public sealed class ConversationMessageMapperTests
{
    [Fact]
    public void ToolResultMessage_MapsToToolRoleWithPreservedResult()
    {
        var mapped = ConversationMessageMapper.TryMap(new ToolResultMessage(
            Id: "m1",
            ToolUseId: "call-1",
            ToolName: "web_search",
            Content: """{"results":[]}""",
            IsError: false,
            Timestamp: DateTimeOffset.UtcNow));

        mapped.Should().NotBeNull();
        mapped!.Role.Should().Be(ChatRole.Tool);
        var result = mapped.Contents.OfType<FunctionResultContent>().Single();
        result.CallId.Should().Be("call-1");
        result.Result.Should().Be("""{"results":[]}""");
    }

    [Fact]
    public void EmptyToolResultMessage_IsSkipped()
    {
        var mapped = ConversationMessageMapper.TryMap(new ToolResultMessage(
            Id: "m1",
            ToolUseId: "call-1",
            ToolName: "web_search",
            Content: "",
            IsError: false,
            Timestamp: DateTimeOffset.UtcNow));

        mapped.Should().BeNull();
    }

    [Fact]
    public void EmptyAssistantMessage_IsSkipped()
    {
        // 零内容 assistant 消息进回放历史只会污染协议（部分模型返回空响应），
        // 新转录已不落库空 assistant 消息（SessionManager 守卫），旧转录靠这里兜底。
        var mapped = ConversationMessageMapper.TryMap(new AssistantMessage(
            Id: "m2",
            Content: [],
            Timestamp: DateTimeOffset.UtcNow));

        mapped.Should().BeNull();
    }

    [Fact]
    public void AssistantToolUse_MapsToFunctionCallContentOnAssistantRole()
    {
        var mapped = ConversationMessageMapper.TryMap(new AssistantMessage(
            Id: "m3",
            Content: [new ToolUseBlock("call-9", "Read", """{"filePath":"a.cs"}""")],
            Timestamp: DateTimeOffset.UtcNow));

        mapped.Should().NotBeNull();
        mapped!.Role.Should().Be(ChatRole.Assistant);
        var call = mapped.Contents.OfType<FunctionCallContent>().Single();
        call.CallId.Should().Be("call-9");
        call.Name.Should().Be("Read");
    }

    [Fact]
    public void ToChatHistory_UnclosedToolCall_SynthesizesErrorFunctionResultToKeepAtomicGroupClosed()
    {
        var now = DateTimeOffset.UtcNow;
        Message[] transcript =
        [
            new UserMessage("u1", "edit file", now),
            new AssistantMessage("a1", [new ToolUseBlock("call-unclosed", "Edit", "{corrupt")], now),
            new UserMessage("u2", "next turn", now),
        ];

        var history = ConversationMessageMapper.ToChatHistory(transcript);

        history.Should().HaveCount(4);
        history[2].Role.Should().Be(ChatRole.Tool);
        var synthesized = history[2].Contents.OfType<FunctionResultContent>().Single();
        synthesized.CallId.Should().Be("call-unclosed");
        synthesized.Result?.ToString().Should().Contain("[TOOL ERROR]");
    }
}
