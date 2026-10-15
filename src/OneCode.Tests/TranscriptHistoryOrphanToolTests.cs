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

    /// <summary>模拟模式指令 / 计划交接上下文的 AIContextProvider 注入。</summary>
    private sealed class ModeContextInjectingProvider : AIContextProvider
    {
        private const string Marker = "[MODE_CONTEXT_MARKER]";

        public static string InjectedText => Marker;

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            AIContextProvider.InvokingContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AIContext { Messages = [new ChatMessage(ChatRole.User, Marker)] });
    }

    [Fact]
    public async Task SecondModelCall_IncludesAssistantToolCallsMessage()
    {
        var client = new MultiCallChatClient();
        var provider = new TranscriptChatHistoryProvider(CreateTranscriptReader(), SessionId.NewId());

        var options = new HarnessAgentOptions
        {
            ChatHistoryProvider = provider,
            AIContextProviders = [new ModeContextInjectingProvider()],
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

    /// <summary>
    /// 三轮以上连续工具调用 + CompactionStrategy + AIContextProvider 守护：
    /// 第 3 次模型调用仍必须完整保留首轮 User 原始提问、第 1 轮与第 2 轮的全部 assistant(tool_calls)/tool 消息对，
    /// 且每轮动态注入的 AIContextProvider 消息不得被回写入账累积。
    /// （防回归：若把 storeInputRequestMessageFilter 误传给 provideOutputMessageFilter 或返回未克隆的 state.Messages 引用，
    /// 基类 InvokingCoreAsync 原地盖上 ChatHistory 标记后，第 3 次调用起会把首轮 User 与早期轮次历史全部滤除。）
    /// </summary>
    [Fact]
    public async Task ThirdModelCall_WithCompactionAndContextProviders_PreservesUserPromptAndFullToolHistoryWithoutContextAccumulation()
    {
        var calls = 0;
        List<IReadOnlyList<ChatMessage>> requests = [];
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var snapshot = callInfo.ArgAt<IEnumerable<ChatMessage>>(0).Select(m => m.Clone()).ToList();
                requests.Add(snapshot);
                calls++;
                AIContent[] content = calls switch
                {
                    1 => [new FunctionCallContent("call-1", "WebSearch", new Dictionary<string, object?> { ["query"] = "q1" })],
                    2 => [new FunctionCallContent("call-2", "WebSearch", new Dictionary<string, object?> { ["query"] = "q2" })],
                    _ => [new TextContent("all done")],
                };
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, content.ToList())));
            });

        var provider = new TranscriptChatHistoryProvider(CreateTranscriptReader(), SessionId.NewId());
        var options = new HarnessAgentOptions
        {
            ChatHistoryProvider = provider,
            AIContextProviders = [new ModeContextInjectingProvider()],
            CompactionStrategy = CompactionPipelineBuilder.BuildForMainAgent(client, 128_000, 4_096),
            ChatOptions = new ChatOptions
            {
                Instructions = "PLAN_PROMPT",
                Tools = [AIFunctionFactory.Create((string query) => $"{{\"query\":\"{query}\"}}", name: "WebSearch")],
                ToolMode = ChatToolMode.Auto,
            },
        };
        OneCodeHarnessDefaults.ApplyProductOptOuts(options);

        var agent = new HarnessAgent(client, options);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await agent.RunAsync(FirstPrompt, session, cancellationToken: TestContext.Current.CancellationToken);

        requests.Should().HaveCount(3, "两次工具调用后应进入第三次模型请求");
        requests[0].Count(m => m.Role == ChatRole.User && m.Text == ModeContextInjectingProvider.InjectedText)
            .Should().Be(1, "首轮请求应包含当次 AIContextProvider 注入的上下文消息");

        var third = requests[2];
        var dump = DumpRequests(third);

        third.Count(m => m.Role == ChatRole.User && m.Text == FirstPrompt)
            .Should().Be(1, $"第 3 次模型请求仍必须保留首轮用户提问且不重复。\n实际序列：\n{dump}");

        var assistantCallIds = third
            .Where(m => m.Role == ChatRole.Assistant)
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(c => c.CallId)
            .ToList();
        assistantCallIds.Should().Equal(["call-1", "call-2"],
            $"第 3 次模型请求必须按序保留前两轮的全部 assistant(tool_calls)。\n实际序列：\n{dump}");

        var toolResultIds = third
            .Where(m => m.Role == ChatRole.Tool)
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(r => r.CallId)
            .ToList();
        toolResultIds.Should().Equal(["call-1", "call-2"],
            $"第 3 次模型请求必须按序保留前两轮的全部 tool 结果。\n实际序列：\n{dump}");

        third.Count(m => m.Role == ChatRole.User && m.Text == ModeContextInjectingProvider.InjectedText)
            .Should().Be(1, $"同回合工具循环期间（Call 3）AIContextProvider 注入的上下文仍须保持可见且仅有一份。\n实际序列：\n{dump}");

        await agent.RunAsync("第二轮问题", session, cancellationToken: TestContext.Current.CancellationToken);
        var secondTurnFirstRequest = requests[3];
        secondTurnFirstRequest.Count(m => m.Role == ChatRole.User && m.Text == FirstPrompt)
            .Should().Be(1, "第二轮对话仍应完整保留首轮用户提问");
    }

    /// <summary>
    /// 模拟主会话同时注入 Todo（User 角色）与 ModeInstruction（System 角色排在末尾）的 AIContextProvider。
    /// </summary>
    private sealed class TodoAndModeContextInjectingProvider : AIContextProvider
    {
        public const string TodoText = "### Current todo list\n- none yet";
        public const string ModeText = "You are in BUILD MODE — default full-capability engineering mode.";

        protected override ValueTask<AIContext> ProvideAIContextAsync(
            AIContextProvider.InvokingContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AIContext
            {
                Messages =
                [
                    new ChatMessage(ChatRole.User, TodoText),
                    new ChatMessage(ChatRole.System, ModeText),
                ],
            });
    }

    /// <summary>
    /// 线上 74 轮死循环防回归守护：
    /// 当主会话同时开启 <c>EnableToolApproval = true</c>（<c>ToolApprovalAgent</c> 自动批准后重新进入
    /// <c>ChatClientAgent.RunStreamingAsync</c> 导致 <c>AIContextProviders</c> 在每次工具迭代都重新注入）、
    /// <c>CompactionStrategy</c> 与末尾 <c>System</c> 模式指令时：
    /// 1. 第 2、3 次模型请求必须包含前序全部 <c>FunctionCallContent</c> 与 <c>FunctionResultContent</c>，
    ///    绝不能因 <c>CompactionMessageIndex.Update</c> 末尾相等短路而退化为首轮初始消息；
    /// 2. <c>FunctionInvokingChatClient.ProcessFunctionApprovalResponses</c> 合成的重复 <c>Assistant(FunctionCallContent)</c>
    ///    必须被去重，每个 <c>callId</c> 的 <c>FunctionCallContent</c> 仅出现一次。
    /// </summary>
    [Fact]
    public async Task ToolApprovalStreamingLoop_WithCompactionAndSystemTailContextProvider_PreservesAllToolCallsAndResultsWithoutShortCircuit()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = 0;
        List<IReadOnlyList<ChatMessage>> requests = [];
        var client = Substitute.For<IChatClient>();
        client.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var snapshot = callInfo.ArgAt<IEnumerable<ChatMessage>>(0).Select(m => m.Clone()).ToList();
                requests.Add(snapshot);
                calls++;
                return StreamUpdatesAsync(calls);
            });

        static async IAsyncEnumerable<ChatResponseUpdate> StreamUpdatesAsync(int callNumber)
        {
            await Task.Yield();
            if (callNumber == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new FunctionCallContent("call-1", "Bash", new Dictionary<string, object?> { ["command"] = "date +%F" }),
                ]);
            }
            else if (callNumber == 2)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new FunctionCallContent("call-2", "Write", new Dictionary<string, object?> { ["path"] = "out.txt", ["content"] = "ok" }),
                ]);
            }
            else
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "created");
            }
        }

        var bashTool = new ApprovalRequiredAIFunction(
            AIFunctionFactory.Create((string command) => "Error: Cannot bind parameter 'Date'.", name: "Bash"));
        var writeTool = new ApprovalRequiredAIFunction(
            AIFunctionFactory.Create((string path, string content) => $"Wrote {content.Length} chars to {path}", name: "Write"));

        var provider = new TranscriptChatHistoryProvider(CreateTranscriptReader(), SessionId.NewId());
        var options = new HarnessAgentOptions
        {
            ChatHistoryProvider = provider,
            AIContextProviders = [new TodoAndModeContextInjectingProvider()],
            CompactionStrategy = CompactionPipelineBuilder.BuildForMainAgent(client, 128_000, 4_096),
            DisableToolAutoApproval = false,
            ToolApprovalAgentOptions = new ToolApprovalAgentOptions
            {
                AutoApprovalRules = [static _ => ValueTask.FromResult(true)],
            },
            ChatOptions = new ChatOptions
            {
                Instructions = "DEFAULT_PROMPT",
                Tools = [bashTool, writeTool],
                ToolMode = ChatToolMode.Auto,
            },
        };
        OneCodeHarnessDefaults.ApplyProductOptOuts(options);

        var agent = new HarnessAgent(client, options);
        var session = await agent.CreateSessionAsync(ct);

        await foreach (var _ in agent.RunStreamingAsync(FirstPrompt, session, cancellationToken: ct))
        {
        }

        requests.Should().HaveCount(3, "两次获批工具调用后应在第三次流式模型请求完成收敛");
        requests[0][^1].Role.Should().Be(ChatRole.User, "首轮请求末尾应为真实用户提问而非 AIContextProvider 注入的 System 消息");
        requests[0][^1].Text.Should().Be(FirstPrompt);

        var second = requests[1];
        var secondDump = DumpRequests(second);
        second[^1].Role.Should().Be(ChatRole.Tool,
            $"第二次请求末尾必须是刚执行的 Tool 结果，不能被 AIContextProvider 消息垫底。\n实际序列：\n{secondDump}");
        second.Where(m => m.Role == ChatRole.Assistant)
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(c => c.CallId)
            .Should().Equal(["call-1"],
                $"第二次请求必须包含且仅包含一次 call-1（审批恢复合成的重复 Assistant 消息须被去重）。\n实际序列：\n{secondDump}");
        second.Where(m => m.Role == ChatRole.Tool)
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(r => r.CallId)
            .Should().Equal(["call-1"],
                $"第二次请求必须包含 call-1 的工具执行结果（绝不能被 CompactionMessageIndex 短路丢弃）。\n实际序列：\n{secondDump}");

        var third = requests[2];
        var thirdDump = DumpRequests(third);
        third[^1].Role.Should().Be(ChatRole.Tool);
        third.Where(m => m.Role == ChatRole.Assistant)
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(c => c.CallId)
            .Should().Equal(["call-1", "call-2"],
                $"第三次请求必须按序包含前两次获批工具调用且无重复。\n实际序列：\n{thirdDump}");
        third.Where(m => m.Role == ChatRole.Tool)
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(r => r.CallId)
            .Should().Equal(["call-1", "call-2"],
                $"第三次请求必须按序包含前两次工具调用结果。\n实际序列：\n{thirdDump}");
    }

    /// <summary>
    /// 跨轮无工具对话（含连续发送相同文本的用户消息）+ CompactionStrategy + AIContextProvider 守护：
    /// 验证第 1 轮无工具调用直接回复后，第 2 轮与第 3 轮（即使第 3 轮与第 2 轮用户文本同为 "继续"）
    /// 均不会因末尾消息 ContentEquals 短路而丢弃前序问答或当前提问。
    /// </summary>
    [Fact]
    public async Task MultiTurnWithoutTools_WithCompactionAndContextProviders_PreservesAllTurnsEvenWhenUserRepeatsSameText()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = 0;
        List<IReadOnlyList<ChatMessage>> requests = [];
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var snapshot = callInfo.ArgAt<IEnumerable<ChatMessage>>(0).Select(m => m.Clone()).ToList();
                requests.Add(snapshot);
                calls++;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, $"reply-{calls}")));
            });

        var provider = new TranscriptChatHistoryProvider(CreateTranscriptReader(), SessionId.NewId());
        var options = new HarnessAgentOptions
        {
            ChatHistoryProvider = provider,
            AIContextProviders = [new TodoAndModeContextInjectingProvider()],
            CompactionStrategy = CompactionPipelineBuilder.BuildForMainAgent(client, 128_000, 4_096),
            ChatOptions = new ChatOptions { Instructions = "DEFAULT_PROMPT" },
        };
        OneCodeHarnessDefaults.ApplyProductOptOuts(options);

        var agent = new HarnessAgent(client, options);
        var session = await agent.CreateSessionAsync(ct);

        await agent.RunAsync(FirstPrompt, session, cancellationToken: ct);
        await agent.RunAsync("继续", session, cancellationToken: ct);
        await agent.RunAsync("继续", session, cancellationToken: ct);

        requests.Should().HaveCount(3);

        var turn2Request = requests[1];
        turn2Request.Select(m => m.Text).Should().ContainInOrder(
            "SYS", FirstPrompt, "reply-1", TodoAndModeContextInjectingProvider.TodoText, TodoAndModeContextInjectingProvider.ModeText, "继续");

        var turn3Request = requests[2];
        turn3Request.Select(m => m.Text).Should().ContainInOrder(
            "SYS", FirstPrompt, "reply-1", "继续", "reply-2", TodoAndModeContextInjectingProvider.TodoText, TodoAndModeContextInjectingProvider.ModeText, "继续");
        turn3Request.Count(m => m.Role == ChatRole.User && m.Text == "继续").Should().Be(2,
            "连续两轮发送相同文本 '继续' 时，两条用户消息有独立 MessageId，不得触发 CompactionMessageIndex 末尾相等短路");
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
