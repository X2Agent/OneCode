using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Session;
using OneCode.Core.Domain;
using OneCode.Tests.TestSupport;

namespace OneCode.Tests;

/// <summary>
/// 多轮历史经 MAF <see cref="ChatHistoryProvider"/> 契约桥接到会话转录：
/// 播种排除本轮输入用户消息；MAF 回写进会话状态后，状态是 run 内唯一新鲜历史来源
/// （工具批次回合结束后才落转录），不再重读转录。
/// </summary>
public sealed class TranscriptChatHistoryProviderTests
{
    private static readonly SessionId ConversationId = SessionId.NewId();

    [Fact]
    public async Task InvokingAsync_ExcludesLatestUserMessageAlreadyInTranscript()
    {
        var reader = CreateReader(
        [
            new ChatMessage(ChatRole.User, "earlier question"),
            new ChatMessage(ChatRole.Assistant, "earlier answer"),
            new ChatMessage(ChatRole.User, "current question"),
        ]);
        var provider = new TranscriptChatHistoryProvider(reader, ConversationId);

        var result = (await provider.InvokingAsync(CreateContext(
            new ChatMessage(ChatRole.User, "current question")))).ToList();

        result.Should().HaveCount(3, "transcript history minus the latest user turn, plus the request message");
        result[0].Text.Should().Be("earlier question");
        result[1].Text.Should().Be("earlier answer");
        result[2].Text.Should().Be("current question");
    }

    [Fact]
    public async Task InvokingAsync_KeepsTranscriptWhenLatestUserDoesNotMatch()
    {
        var reader = CreateReader(
        [
            new ChatMessage(ChatRole.User, "earlier question"),
            new ChatMessage(ChatRole.Assistant, "earlier answer"),
        ]);
        var provider = new TranscriptChatHistoryProvider(reader, ConversationId);

        var result = (await provider.InvokingAsync(CreateContext(
            new ChatMessage(ChatRole.User, "different question")))).ToList();

        result.Should().HaveCount(3);
        result[2].Text.Should().Be("different question");
    }

    [Fact]
    public async Task InvokingAsync_EmptyTranscript_ReturnsOnlyRequestMessages()
    {
        var reader = CreateReader([]);
        var provider = new TranscriptChatHistoryProvider(reader, ConversationId);

        var result = (await provider.InvokingAsync(CreateContext(
            new ChatMessage(ChatRole.User, "first question")))).ToList();

        result.Should().ContainSingle().Which.Text.Should().Be("first question");
    }

    /// <summary>
    /// Harness 的 per-service-call 契约：服务调用回写的消息进入会话状态，下一次
    /// InvokingAsync 直接以状态作答、不再重读转录——工具批次在回合结束后才落转录，
    /// 重读反而丢失在途消息（孤儿 tool 消息的根因，见 TranscriptHistoryOrphanToolTests）。
    /// </summary>
    [Fact]
    public async Task StoreThenSecondInvoke_UsesStateWithoutRereadingTranscript()
    {
        var reader = CreateReader(
        [
            new ChatMessage(ChatRole.System, "SYS"),
            new ChatMessage(ChatRole.User, "TASK"),
        ]);
        var provider = new TranscriptChatHistoryProvider(reader, ConversationId);
        var session = await CreateSessionAsync();

        // run 启动：播种（去掉本轮输入用户消息），RequestMessages 由 MAF 契约拼接在后
        var first = (await provider.InvokingAsync(CreateSessionContext(
            session, new ChatMessage(ChatRole.User, "TASK")))).ToList();
        first.Should().HaveCount(2);
        first[0].Text.Should().Be("SYS", "播种只含系统消息——本轮输入用户消息经 RequestMessages 送达");
        first[1].Text.Should().Be("TASK");

        // 服务调用 1 成功：回写请求 user + 响应 assistant(tool_calls)
        await provider.InvokedAsync(CreateInvoked(session,
            requestMessages: [new ChatMessage(ChatRole.User, "TASK")],
            responseMessages: [AssistantWithToolCall()]));
        // 服务调用 2 成功：回写请求 tool + 响应最终 assistant
        await provider.InvokedAsync(CreateInvoked(session,
            requestMessages: [ToolResult()],
            responseMessages: [new ChatMessage(ChatRole.Assistant, "done")]));

        // 下一次 Invoking（如工具迭代后的模型调用）：状态即真值
        var second = (await provider.InvokingAsync(CreateSessionContext(
            session, ToolResult()))).ToList();

        second.Select(message => message.Role).Should().ContainInOrder(
            ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant);
        second[2].Contents.OfType<FunctionCallContent>().Should()
            .ContainSingle(call => call.CallId == "call-1");
        reader.ReceivedCalls().Should().HaveCount(1, "状态非空后不得重读转录");
    }

    /// <summary>失败路径：基类契约保证异常调用不回写，状态保持上次成功调用的内容。</summary>
    [Fact]
    public async Task InvokedFailure_DoesNotStore()
    {
        var reader = CreateReader([]);
        var provider = new TranscriptChatHistoryProvider(reader, ConversationId);
        var session = await CreateSessionAsync();

        await provider.InvokedAsync(new ChatHistoryProvider.InvokedContext(
            Substitute.For<AIAgent>(), session,
            [new ChatMessage(ChatRole.User, "failed turn")],
            new InvalidOperationException("stream stalled")));

        var provided = (await provider.InvokingAsync(CreateSessionContext(
            session, new ChatMessage(ChatRole.User, "next turn")))).ToList();

        provided.Should().ContainSingle().Which.Text.Should().Be("next turn",
            "失败调用不回写，状态仍为空 → 重新从转录播种");
    }

    private static ISessionChatHistoryReader CreateReader(IReadOnlyList<ChatMessage> history)
    {
        var reader = Substitute.For<ISessionChatHistoryReader>();
        reader.GetChatHistory(ConversationId).Returns(history);
        return reader;
    }

    /// <summary>StateBag 需要 session；provider 单测经裸 agent 创建真实 session。</summary>
    private static async Task<AgentSession> CreateSessionAsync()
    {
        var agent = new ChatClientAgent(new CapturingChatClient(), new ChatClientAgentOptions());
        return await agent.CreateSessionAsync();
    }

    private static ChatMessage AssistantWithToolCall() => new(ChatRole.Assistant,
    [
        new FunctionCallContent("call-1", "WebSearch", new Dictionary<string, object?> { ["query"] = "q" }),
    ]);

    private static ChatMessage ToolResult() =>
        new(ChatRole.Tool, [new FunctionResultContent("call-1", "{\"ok\":true}")]);

    private static ChatHistoryProvider.InvokingContext CreateContext(
        params ChatMessage[] requestMessages)
        => new(Substitute.For<AIAgent>(), session: null, requestMessages);

    private static ChatHistoryProvider.InvokingContext CreateSessionContext(
        AgentSession? session, params ChatMessage[] requestMessages)
        => new(Substitute.For<AIAgent>(), session, requestMessages);

    private static ChatHistoryProvider.InvokedContext CreateInvoked(
        AgentSession? session, ChatMessage[] requestMessages, ChatMessage[] responseMessages)
        => new(Substitute.For<AIAgent>(), session, requestMessages, responseMessages);
}
