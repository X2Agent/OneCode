using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Session;
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

    /// <summary>
    /// 阶段边界播种：保留边界之前的 Plan 阶段对话与只读工具结果（如 WebSearch），
    /// 仅成对剥离边界之前的 SubmitPlan 调用与结果，同时保留边界之后的同阶段恢复消息。
    /// </summary>
    [Fact]
    public async Task InvokingAsync_WithSeedBoundary_RetainsPlanResearchAndStripsSubmitPlanPair()
    {
        var reader = CreateReader(
        [
            new ChatMessage(ChatRole.User, "plan-stage question"),
            AssistantWithToolCall(),
            ToolResult(),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-submit", "SubmitPlan")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-submit", "Plan submitted")]),
            new ChatMessage(ChatRole.Assistant, "build attempt message"),
        ]);
        var provider = new TranscriptChatHistoryProvider(
            reader, ConversationId, new HistorySeedBoundary("req-1", 0, 5));

        var result = (await provider.InvokingAsync(CreateContext(
            new ChatMessage(ChatRole.User, "execute approved plan")))).ToList();

        result.Should().HaveCount(5,
            "plan-stage user prompt + WebSearch call/result + post-boundary build message + current request must all be present, with the SubmitPlan pair stripped");
        result[0].Text.Should().Be("plan-stage question");
        result[1].Contents.OfType<FunctionCallContent>().Single().Name.Should().Be("WebSearch");
        result[2].Contents.OfType<FunctionResultContent>().Single().CallId.Should().Be("call-1");
        result[3].Text.Should().Be("build attempt message");
        result[4].Text.Should().Be("execute approved plan");
        result.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .Should().NotContain(c => c.Name == "SubmitPlan");
        result.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Should().NotContain(r => r.CallId == "call-submit");
    }

    /// <summary>混合消息剥离：当同一条 assistant 消息同时含文本与 SubmitPlan 调用时，仅剔除 SubmitPlan 调用并保留文本。</summary>
    [Fact]
    public async Task InvokingAsync_WithSeedBoundary_MixedAssistantMessage_PreservesNonSubmitPlanContent()
    {
        var reader = CreateReader(
        [
            new ChatMessage(ChatRole.User, "plan-stage question"),
            new ChatMessage(ChatRole.Assistant,
            [
                new TextContent("I have verified the scope."),
                new FunctionCallContent("call-submit", "SubmitPlan"),
            ]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-submit", "Plan submitted")]),
        ]);
        var provider = new TranscriptChatHistoryProvider(
            reader, ConversationId, new HistorySeedBoundary("req-1", 0, 3));

        var result = (await provider.InvokingAsync(CreateContext(
            new ChatMessage(ChatRole.User, "execute approved plan")))).ToList();

        result.Should().HaveCount(3);
        result[0].Text.Should().Be("plan-stage question");
        result[1].Text.Should().Be("I have verified the scope.");
        result[1].Contents.OfType<FunctionCallContent>().Should().BeEmpty();
        result[2].Text.Should().Be("execute approved plan");
    }

    [Fact]
    public async Task InvokingAsync_WithoutSeedBoundary_WhenStripPlanExclusiveToolsTrue_StripsSubmitPlanPair()
    {
        var reader = CreateReader(
        [
            new ChatMessage(ChatRole.User, "plan-stage question after compact"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-submit", "SubmitPlan")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-submit", "Plan submitted")]),
            new ChatMessage(ChatRole.Assistant, "ready to build"),
        ]);
        var provider = new TranscriptChatHistoryProvider(
            reader, ConversationId, seedBoundary: null, stripPlanExclusiveTools: true);

        var result = (await provider.InvokingAsync(CreateContext(
            new ChatMessage(ChatRole.User, "execute approved plan")))).ToList();

        result.Should().HaveCount(3,
            "even when HistorySeedBoundary is invalidated by /compact epoch drift, non-Plan runs must strip SubmitPlan pairs");
        result.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
            .Should().NotContain(c => c.Name == "SubmitPlan");
        result.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Should().NotContain(r => r.CallId == "call-submit");
    }

    /// <summary>
    /// 流式推理模型产生的空 <see cref="TextContent"/> 块在回写与读取时必须被剔除，
    /// 保留非空文本、推理内容与工具调用，避免请求体出现连续的 <c>{"type":"text","text":""}</c>。
    /// </summary>
    [Fact]
    public async Task InvokedThenInvoking_StripsEmptyTextContentBlocks()
    {
        var reader = CreateReader([]);
        var provider = new TranscriptChatHistoryProvider(reader, ConversationId);
        var session = await CreateSessionAsync();

        var assistantMessage = new ChatMessage(ChatRole.Assistant,
        [
            new TextContent(""),
            new TextContent(""),
            new TextReasoningContent("thinking about query"),
            new TextContent("Here is the result."),
            new TextContent(""),
            new FunctionCallContent("call-1", "WebSearch", new Dictionary<string, object?> { ["query"] = "q" }),
        ]);

        await provider.InvokedAsync(CreateInvoked(
            session,
            requestMessages: [new ChatMessage(ChatRole.User, "search news")],
            responseMessages: [assistantMessage]));

        var history = (await provider.InvokingAsync(CreateSessionContext(
            session, ToolResult()))).ToList();

        var storedAssistant = history.Single(m => m.Role == ChatRole.Assistant);
        storedAssistant.Contents.OfType<TextContent>().Should()
            .ContainSingle().Which.Text.Should().Be("Here is the result.");
        storedAssistant.Contents.OfType<TextReasoningContent>().Should()
            .ContainSingle().Which.Text.Should().Be("thinking about query");
        storedAssistant.Contents.OfType<FunctionCallContent>().Should()
            .ContainSingle().Which.CallId.Should().Be("call-1");
    }

    [Fact]
    public async Task SeedFromTranscript_BeforePlanBoundary_CompactsOversizedFailedToolResultsAndKeepsSuccessIntact()
    {
        var longError = "[ERROR] WebSearch error: all providers failed.\n" + new string('e', 500);
        var longSuccess = "Successfully fetched article body: " + new string('s', 500);

        List<ChatMessage> transcript =
        [
            new(ChatRole.User, "plan research"),
            new(ChatRole.Assistant,
            [
                new FunctionCallContent("call-err", "WebSearch", new Dictionary<string, object?> { ["query"] = "q1" }),
                new FunctionCallContent("call-ok", "WebFetch", new Dictionary<string, object?> { ["url"] = "https://example.com" }),
                new FunctionCallContent("call-plan", "SubmitPlan", new Dictionary<string, object?> { ["plan"] = "# Plan" }),
            ]),
            new(ChatRole.Tool,
            [
                new FunctionResultContent("call-err", longError),
                new FunctionResultContent("call-ok", longSuccess),
                new FunctionResultContent("call-plan", "Plan submitted"),
            ]),
            new(ChatRole.User, "build handoff"),
        ];

        var reader = CreateReader(transcript);
        var boundary = new HistorySeedBoundary("req-1", 0, 3);
        var provider = new TranscriptChatHistoryProvider(reader, ConversationId, boundary);
        var session = await CreateSessionAsync();

        var seeded = (await provider.InvokingAsync(CreateSessionContext(
            session, new ChatMessage(ChatRole.User, "build handoff")))).ToList();

        var toolMsg = seeded.Single(m => m.Role == ChatRole.Tool);
        var results = toolMsg.Contents.OfType<FunctionResultContent>().ToList();
        results.Should().HaveCount(2, "SubmitPlan result is stripped while other tool results remain");

        var errResult = results.Single(r => r.CallId == "call-err").Result?.ToString();
        errResult.Should().Contain("[prior-phase error compacted]");
        errResult!.Length.Should().BeLessThan(300);

        var okResult = results.Single(r => r.CallId == "call-ok").Result?.ToString();
        okResult.Should().Be(longSuccess, "successful tool results must be 100% preserved without truncation");
    }

    /// <summary>
    /// 单元契约：AIContextProvider 注入的瞬态消息必须锚定在最后一条真实 User 消息紧邻之前（确保首轮末尾是 User、
    /// 工具迭代末尾是 Tool），且 ToolApprovalAgent 重入时由 FunctionInvokingChatClient 合成的重复 Assistant(tool_call)
    /// 在读取与回写两侧均须去重。
    /// </summary>
    [Fact]
    public async Task InvokingAsync_AnchorsTurnContextBeforeLatestUserAndDeduplicatesSynthesizedToolCallOnApprovalReentry()
    {
        var reader = CreateReader([new ChatMessage(ChatRole.System, "SYS")]);
        var provider = new TranscriptChatHistoryProvider(reader, ConversationId);
        var session = await CreateSessionAsync();

        var userPrompt = new ChatMessage(ChatRole.User, "create file");
        var todoContext = new ChatMessage(ChatRole.User, "### Current todo list")
            .WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, "TodoProvider");
        var modeContext = new ChatMessage(ChatRole.System, "You are in BUILD MODE")
            .WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, "ModeProvider");

        // Call 1：初始请求携带 User 提问与末尾两条 AIContextProvider 消息
        var call1Messages = (await provider.InvokingAsync(
            CreateSessionContext(session, userPrompt, todoContext, modeContext))).ToList();

        call1Messages.Select(m => m.Text).Should().Equal(
            "SYS", "### Current todo list", "You are in BUILD MODE", "create file");
        call1Messages[^1].Role.Should().Be(ChatRole.User, "首轮请求末尾必须是真实 User 提问");

        // Call 1 结束回写：持久化 User 提问与模型返回的原始 Assistant(tool_call)
        var originalAssistantCall = AssistantWithToolCall();
        await provider.InvokedAsync(CreateInvoked(
            session,
            requestMessages: call1Messages.ToArray(),
            responseMessages: [originalAssistantCall]));

        // Call 2（ToolApprovalAgent 审批重入）：FICC 合成了一条同 CallId 的 Assistant(tool_call) + Tool 结果，
        // 且 ChatClientAgent 重新追加了两条 AIContextProvider 消息
        var synthesizedDuplicateAssistant = AssistantWithToolCall();
        var toolResult = ToolResult();
        var call2TodoContext = new ChatMessage(ChatRole.User, "### Current todo list")
            .WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, "TodoProvider");
        var call2ModeContext = new ChatMessage(ChatRole.System, "You are in BUILD MODE")
            .WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, "ModeProvider");

        var call2Messages = (await provider.InvokingAsync(
            CreateSessionContext(session, synthesizedDuplicateAssistant, toolResult, call2TodoContext, call2ModeContext))).ToList();

        call2Messages.Select(m => m.Role).Should().Equal(
            ChatRole.System,
            ChatRole.User,
            ChatRole.System,
            ChatRole.User,
            ChatRole.Assistant,
            ChatRole.Tool);
        call2Messages[^1].Role.Should().Be(ChatRole.Tool, "审批重入后的工具迭代末尾必须是最新 Tool 结果");
        call2Messages.Count(m => m.Role == ChatRole.Assistant).Should().Be(1,
            "FICC 在审批恢复时合成的同 CallId Assistant(tool_call) 必须被去重");

        // Call 2 结束回写后，再次调用也不应累积重复的 Assistant(tool_call)
        await provider.InvokedAsync(CreateInvoked(
            session,
            requestMessages: [synthesizedDuplicateAssistant, toolResult, call2TodoContext, call2ModeContext],
            responseMessages: [new ChatMessage(ChatRole.Assistant, "final answer")]));

        var nextTurnMessages = (await provider.InvokingAsync(
            CreateSessionContext(session, new ChatMessage(ChatRole.User, "follow up")))).ToList();
        nextTurnMessages.Where(m => m.Role == ChatRole.Assistant)
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(c => c.CallId)
            .Should().Equal(["call-1"], "回写侧同样必须过滤重复合成的 Assistant(tool_call)");
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
