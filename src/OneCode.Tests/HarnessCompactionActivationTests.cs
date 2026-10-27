#pragma warning disable MAAI001 // MAF 实验性 API（压缩激活真实包测试）：升级 MAF 时按升级复查清单回归
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace OneCode.Tests;

/// <summary>
/// 框架行为守卫：锁定「Harness per-service-call 历史持久化下，压缩在每次服务调用都激活」这一 MAF 行为。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要这组测试</b>：<c>HarnessAgent</c> 强制 <c>RequirePerServiceCallChatHistoryPersistence = true</c>，
/// 该装饰器在框架自管历史的路径上会把哨兵值 <c>_agent_local_chat_history</c> 写进
/// <c>ChatClientAgentSession.ConversationId</c>。MAF 曾把「ConversationId 非空」解读为「历史由远端服务托管」
/// 并整个跳过压缩，导致一个会话里压缩只在第一次服务调用执行、之后永久失效；MAF 1.24 起允许对
/// per-service-call 本地历史压缩，哨兵不再抑制 <c>CompactionProvider</c>。
/// </para>
/// <para>
/// 该行为不是产品可配置项，因此这里断言的是<b>框架现状</b>而非期望值。MAF 升级后若行为改变，
/// 这两个用例会失败，提示重新评估压缩装配。
/// </para>
/// </remarks>
public sealed class HarnessCompactionActivationTests
{
    private const string LocalHistorySentinel = "_agent_local_chat_history";

    /// <summary>始终触发的计数策略——只记录被调用次数，不改动消息。</summary>
    private sealed class CountingCompactionStrategy() : CompactionStrategy(CompactionTriggers.Always)
    {
        public int Invocations { get; private set; }

        protected override ValueTask<bool> CompactCoreAsync(
            CompactionMessageIndex index, ILogger logger, CancellationToken cancellationToken)
        {
            Invocations++;
            return new ValueTask<bool>(false);
        }
    }

    /// <summary>
    /// 首个请求回一个工具调用，逼出第二次服务调用；之后回文本收尾。
    /// 这样单次 agent run 内就有两次服务调用，能区分「每次服务调用压缩」与「每次 run 压缩一次」。
    /// </summary>
    private sealed class ToolLoopChatClient : IChatClient
    {
        public int ServiceCalls { get; private set; }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ServiceCalls++;

            var alreadyRanTool = messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any());
            AIContent content = alreadyRanTool
                ? new TextContent("done")
                : new FunctionCallContent($"call{ServiceCalls}", "echo", new Dictionary<string, object?> { ["text"] = "hi" });

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [content])));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }
    }

    private static HarnessAgent CreateHarnessAgent(IChatClient chatClient, CompactionStrategy strategy) =>
        new(chatClient, new HarnessAgentOptions
        {
            Name = "compaction-probe",
            // 与产品主路径一致：显式 InMemory 历史（不带 reducer）+ 产品策略交给 Harness。
            ChatOptions = new ChatOptions { Tools = [AIFunctionFactory.Create((string text) => text, name: "echo")] },
            ChatHistoryProvider = new InMemoryChatHistoryProvider(),
            CompactionStrategy = strategy,
            DisableToolAutoApproval = true,
            DisableOpenTelemetry = true,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableAgentSkillsProvider = true,
            DisableFileMemory = true,
            DisableWebSearch = true,
        });

    private static IReadOnlyList<ChatMessage> SeedMessages() =>
        [new(ChatRole.User, "one"), new(ChatRole.User, "two"), new(ChatRole.User, "three")];

    [Fact]
    public async Task Compaction_UnderHarness_RunsOnEveryServiceCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var strategy = new CountingCompactionStrategy();
        var chatClient = new ToolLoopChatClient();
        var agent = CreateHarnessAgent(chatClient, strategy);
        var session = await agent.CreateSessionAsync(ct);

        await agent.RunAsync(SeedMessages(), session, cancellationToken: ct);
        await agent.RunAsync("four", session, cancellationToken: ct);

        chatClient.ServiceCalls.Should().Be(3, "two calls for the tool loop in run 1, one more in run 2");
        strategy.Invocations.Should().Be(3,
            "MAF 1.24 allows compaction for per-service-call local chat history — " +
            "the local-history sentinel no longer suppresses CompactionProvider");
        session.GetService<ChatClientAgentSession>()!.ConversationId.Should().Be(LocalHistorySentinel);
    }

    /// <summary>流式是产品主路径，必须确认它没有走到不同的分支上。</summary>
    [Fact]
    public async Task Compaction_UnderHarnessStreaming_RunsOnEveryServiceCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var strategy = new CountingCompactionStrategy();
        var chatClient = new ToolLoopChatClient();
        var agent = CreateHarnessAgent(chatClient, strategy);
        var session = await agent.CreateSessionAsync(ct);

        await foreach (var _ in agent.RunStreamingAsync(SeedMessages(), session, cancellationToken: ct)) { }
        await foreach (var _ in agent.RunStreamingAsync("four", session, cancellationToken: ct)) { }

        chatClient.ServiceCalls.Should().Be(3);
        strategy.Invocations.Should().Be(3, "streaming takes the same compaction path as the non-streaming call");
        session.GetService<ChatClientAgentSession>()!.ConversationId.Should().Be(LocalHistorySentinel);
    }

    /// <summary>
    /// 对照：拆掉 Harness 的 per-service-call 装饰器，改用裸 <c>ChatClientAgent</c> 装配——
    /// 锁定无哨兵路径上压缩同样在每一次服务调用执行，防止两条装配路径漂移出不同行为。
    /// </summary>
    [Fact]
    public async Task Compaction_WithoutPerServiceCallPersistence_RunsOnEveryServiceCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var strategy = new CountingCompactionStrategy();
        var chatClient = new ToolLoopChatClient();

        var agent = chatClient.AsBuilder()
            .UseFunctionInvocation()
            .UseAIContextProviders(new CompactionProvider(strategy))
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Name = "compaction-probe-counterfactual",
                ChatOptions = new ChatOptions { Tools = [AIFunctionFactory.Create((string text) => text, name: "echo")] },
                ChatHistoryProvider = new InMemoryChatHistoryProvider(),
                UseProvidedChatClientAsIs = true,
            });

        var session = await agent.CreateSessionAsync(ct);

        await agent.RunAsync(SeedMessages(), session, cancellationToken: ct);
        await agent.RunAsync("four", session, cancellationToken: ct);

        chatClient.ServiceCalls.Should().Be(3);
        strategy.Invocations.Should().Be(3, "without the sentinel, every service call is compacted");
    }
}
