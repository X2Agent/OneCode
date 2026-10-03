using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Session;
using OneCode.Infrastructure.Agent;
using OneCode.Core.Domain;
using System.Runtime.CompilerServices;

namespace OneCode.Tests;

/// <summary>
/// 管线级守护：多轮工具调用时，第二轮模型请求必须包含 assistant(tool_calls) 消息——
/// 否则 tool 结果成为孤儿消息，严格上游（OpenRouter stealth 模型实测）确定性拒绝，
/// 表现为 HTTP 200 + 空 SSE。
///
/// 根因（2026-10-01）：HarnessAgent 硬编码 per-service-call 历史持久化，FICC 收到哨兵
/// ConversationId 后在工具迭代间清空自身缓冲，assistant/tool 消息只经 InvokedAsync 回写
/// 到达 ChatHistoryProvider。<see cref="TranscriptChatHistoryProvider"/> 实现回写侧后，
/// 第二轮请求由"会话状态（转录播种 + 回写增量）+ FICC 增量"合成，结构完整。
/// 本测试全部使用真实 provider，reader 用转录的 run 启动状态模拟。
/// </summary>
public sealed class TranscriptHistoryOrphanToolTests
{
    private static readonly string FirstPrompt = "帮我梳理新闻";

    private sealed class MultiCallChatClient : IChatClient
    {
        private int _calls;
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            Requests.Add(messages.ToList());
            _calls++;
            var content = _calls == 1
                ? new AIContent[] { new FunctionCallContent("call-1", "WebSearch", new Dictionary<string, object?> { ["query"] = "q" }) }
                : new AIContent[] { new TextContent("done") };
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, content.ToList())));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(messages.ToList());
            _calls++;
            await Task.Yield();
            if (_calls == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
                {
                    new FunctionCallContent("call-1", "WebSearch", new Dictionary<string, object?> { ["query"] = "q" }),
                });
            }
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }
        }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    /// <summary>转录的 run 启动状态：系统消息 + 本轮输入用户消息（QueryStreamEngine 在 run 前落库）。</summary>
    private static ISessionChatHistoryReader CreateTranscriptReader()
    {
        var reader = Substitute.For<ISessionChatHistoryReader>();
        reader.GetChatHistory(Arg.Any<SessionId>()).Returns(
        [
            new ChatMessage(ChatRole.System, "SYS"),
            new ChatMessage(ChatRole.User, FirstPrompt),
        ]);
        return reader;
    }

    /// <summary>模拟 TodoProjectionService 的用户消息注入。</summary>
    private sealed class TodoInjectingProvider : AIContextProvider
    {
        protected override ValueTask<AIContext> ProvideAIContextAsync(
            AIContextProvider.InvokingContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AIContext { Messages = [new ChatMessage(ChatRole.User, "### Current todo list\n- none yet")] });
    }

    [Fact]
    public async Task SecondModelCall_IncludesAssistantToolCallsMessage()
    {
        var client = new MultiCallChatClient();
        var provider = new TranscriptChatHistoryProvider(CreateTranscriptReader(), SessionId.NewId());

        var options = new HarnessAgentOptions
        {
            ChatHistoryProvider = provider,
            AIContextProviders = [new TodoInjectingProvider()],
            ChatOptions = new ChatOptions
            {
                Instructions = "PLAN_PROMPT",
                Tools = [AIFunctionFactory.Create(() => "{\"ok\":true}", name: "WebSearch")],
                ToolMode = ChatToolMode.Auto,
            },
        };
        OneCodeHarnessDefaults.ApplyProductOptOuts(options);

        var agent = new HarnessAgent(client, options);
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync(FirstPrompt, session);

        client.Requests.Count.Should().BeGreaterThanOrEqualTo(2, "工具调用后应有第二轮模型请求");

        var second = client.Requests[1];
        var dump = DumpRequests(second);

        var assistantIndex = FindLastAssistantWithToolCalls(second);
        var toolIndex = IndexOfRole(second, ChatRole.Tool);

        assistantIndex.Should().BeGreaterThanOrEqualTo(0,
            $"第二轮请求必须包含 assistant(tool_calls) 消息，否则 tool 结果是孤儿，严格上游会确定性拒绝。\n实际序列：\n{dump}");
        var assistantCallIds = second[assistantIndex].Contents
            .OfType<FunctionCallContent>().Select(call => call.CallId).ToList();
        var toolCallIds = second[toolIndex].Contents
            .OfType<FunctionResultContent>().Select(result => result.CallId).ToList();

        toolIndex.Should().BeGreaterThan(assistantIndex, "tool 结果必须紧跟 assistant(tool_calls) 之后");
        toolCallIds.Should().NotBeEmpty().And.OnlyContain(id => assistantCallIds.Contains(id),
            "每个 tool 结果的 callId 都必须能在 assistant 消息中找到对应调用");
    }

    /// <summary>对照：裸 ChatClientAgent（默认无 per-service-call 持久化，FICC 自行累积历史）。</summary>
    [Fact]
    public async Task BareChatClientAgent_SecondCall_KeepsAssistantToolCallsFromHistory()
    {
        var client = new MultiCallChatClient();
        var provider = new TranscriptChatHistoryProvider(CreateTranscriptReader(), SessionId.NewId());

        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            ChatHistoryProvider = provider,
            ChatOptions = new ChatOptions
            {
                Tools = [AIFunctionFactory.Create(() => "{\"ok\":true}", name: "WebSearch")],
                ToolMode = ChatToolMode.Auto,
            },
        });
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync(FirstPrompt, session);

        client.Requests.Count.Should().BeGreaterThanOrEqualTo(2);
        var second = client.Requests[1];
        var dump = DumpRequests(second);

        FindLastAssistantWithToolCalls(second).Should().BeGreaterThanOrEqualTo(0,
            $"裸 ChatClientAgent 也不应丢弃历史中的 assistant(tool_calls)。\n实际序列：\n{dump}");
    }

    /// <summary>对照：HarnessAgent 不带 AIContextProvider 时同样必须结构完整。</summary>
    [Fact]
    public async Task HarnessAgent_WithoutContextProviders_SecondCall_KeepsAssistantToolCalls()
    {
        var client = new MultiCallChatClient();
        var provider = new TranscriptChatHistoryProvider(CreateTranscriptReader(), SessionId.NewId());

        var options = new HarnessAgentOptions
        {
            ChatHistoryProvider = provider,
            ChatOptions = new ChatOptions
            {
                Tools = [AIFunctionFactory.Create(() => "{\"ok\":true}", name: "WebSearch")],
                ToolMode = ChatToolMode.Auto,
            },
        };
        OneCodeHarnessDefaults.ApplyProductOptOuts(options);

        var agent = new HarnessAgent(client, options);
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync(FirstPrompt, session);

        client.Requests.Count.Should().BeGreaterThanOrEqualTo(2);
        var second = client.Requests[1];
        var dump = DumpRequests(second);

        FindLastAssistantWithToolCalls(second).Should().BeGreaterThanOrEqualTo(0,
            $"HarnessAgent 无 AIContextProvider 也不应丢弃。\n实际序列：\n{dump}");
    }

    /// <summary>
    /// 跨轮守护：第二轮对话（同一 session 恢复）的首次请求必须携带上一轮完整交换，
    /// 且上一轮消息不得重复——状态是 run 内真值，播种只发生在状态为空时。
    /// </summary>
    [Fact]
    public async Task SecondTurn_FirstRequestCarriesFullPreviousExchangeWithoutDuplicates()
    {
        var client = new MultiCallChatClient();
        var provider = new TranscriptChatHistoryProvider(CreateTranscriptReader(), SessionId.NewId());

        var options = new HarnessAgentOptions
        {
            ChatHistoryProvider = provider,
            ChatOptions = new ChatOptions
            {
                Tools = [AIFunctionFactory.Create(() => "{\"ok\":true}", name: "WebSearch")],
                ToolMode = ChatToolMode.Auto,
            },
        };
        OneCodeHarnessDefaults.ApplyProductOptOuts(options);

        var agent = new HarnessAgent(client, options);
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync(FirstPrompt, session);
        var runCountAfterFirstTurn = client.Requests.Count;

        await agent.RunAsync("第二轮问题", session);

        var firstOfSecondTurn = client.Requests[runCountAfterFirstTurn];
        var dump = DumpRequests(firstOfSecondTurn)
            + "\n--- run1 req2 ---\n" + DumpRequests(client.Requests[1]);

        var userTexts = firstOfSecondTurn
            .Where(message => message.Role == ChatRole.User)
            .Select(message => message.Text).ToList();
        FindLastAssistantWithToolCalls(firstOfSecondTurn).Should().BeGreaterThanOrEqualTo(0,
            $"跨轮请求必须携带上一轮的 assistant(tool_calls)。\n实际序列：\n{dump}");
        userTexts.Count(text => text == FirstPrompt).Should().Be(1,
            $"上一轮用户消息只出现一次。\n实际序列：\n{dump}");
        firstOfSecondTurn[^1].Role.Should().Be(ChatRole.User, "本轮输入位于请求末尾");
    }

    private static string DumpRequests(IReadOnlyList<ChatMessage> messages)
        => string.Join("\n", messages.Select((message, index) =>
            $"  [{index}] {message.Role}: {string.Join("|", message.Contents.Select(c => c.GetType().Name))}"));

    private static int FindLastAssistantWithToolCalls(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.Assistant
                && messages[i].Contents.OfType<FunctionCallContent>().Any())
                return i;
        }
        return -1;
    }

    private static int IndexOfRole(IReadOnlyList<ChatMessage> messages, ChatRole role)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == role)
                return i;
        }
        return -1;
    }
}
