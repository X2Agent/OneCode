using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OneCode.Core.Permissions;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

/// <summary>
/// 真实装配链路上的审批边界契约：权限层判定 Ask 之后，工具调用到底会不会被拦住。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要这一层：<see cref="ToolApprovalMarkerTests"/> 用裸 <see cref="HarnessAgent"/> 证明「标记 →
/// 审批请求」这一步成立，但它绕过了 <see cref="AgentPipelineBuilder"/>。真实链路里标记有两个施加点
/// （构建期的产品工具目录、请求期的上下文提供器），并且 <c>PermissionAndLimitMiddleware</c> 会先给出
/// Allow/Deny/Ask 再放行。任何一处接线断掉，Ask 都会静默退化成「直接执行」——这正是本文件要守的行为，
/// 且只有经真实装配才能观察到：运行期注入的工具在构建期还不存在。
/// </para>
/// <para>
/// 断言分工：本文件覆盖「Ask 之后发生了什么」，因此使用真实 FICC、真实 <c>ToolApprovalAgent</c>、
/// 真实审批响应绑定，仅把模型输出脚本化（不依赖外网）。权限检查器是可控桩，用于稳定产生 Ask 决策。
/// </para>
/// </remarks>
public sealed class RealStackToolApprovalTests
{
    private const string ProviderToolName = "todos_add";
    private const string ProductToolName = "write_report";
    private const string NeverModeToolName = "internal_scan";
    private const string BenignToolName = "read_only_probe";

    /// <summary>
    /// 运行期注入的工具（Harness 待办清单）必须带审批边界：没有自动批准规则时，
    /// 调用被扣下变成审批请求，而不是直接执行。
    /// </summary>
    /// <remarks>
    /// 反证：删掉 <see cref="ToolApprovalMarkingContextProvider"/> 后本用例必红——工具要么被
    /// 中间件 fail-closed 拒绝（无标记，不产生审批请求），要么直接执行（待办被写入）。
    /// </remarks>
    [Fact]
    public async Task ProviderTool_WithNoAutoApprovalRule_IsHeldForApproval()
    {
        var client = new ScriptedChatClient(ProviderToolName, TodosArguments("Review approval boundary"));
        var (agent, _) = BuildHarness(client, enableTodo: true, autoApprovalRules: []);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var response = await RunAsync(agent, session);

        response.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>()
            .Should().ContainSingle()
            .Which.ToolCall.Should().BeOfType<FunctionCallContent>()
            .Which.Name.Should().Be(ProviderToolName, "the runtime marker must reach the framework's approval decision");

        var provider = RequireTodoProvider(agent);
        (await provider.GetAllTodosAsync(session, TestContext.Current.CancellationToken))
            .Should().BeEmpty("a call held for approval must not have run");
    }

    /// <summary>
    /// 同一工具在生产自动批准规则下静默执行：审批边界存在，但待办清单属于 agent 自己的会话状态，
    /// 逐次弹窗会让审批能力不可用。
    /// </summary>
    [Fact]
    public async Task ProviderTool_WithProductRules_ExecutesSilently()
    {
        var client = new ScriptedChatClient(ProviderToolName, TodosArguments("Review approval boundary"));
        // null 走生产工厂（含 provider 工具规则）；空数组才是「显式清空规则」。
        var (agent, _) = BuildHarness(client, enableTodo: true, autoApprovalRules: null);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var response = await RunAsync(agent, session);

        response.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>()
            .Should().BeEmpty("provider bookkeeping tools auto-approve by name");
        var provider = RequireTodoProvider(agent);
        (await provider.GetAllTodosAsync(session, TestContext.Current.CancellationToken))
            .Should().ContainSingle("the tool must have really executed, not merely been approved")
            .Which.Title.Should().Be("Review approval boundary");
    }

    /// <summary>构建期标记的产品工具同样被扣下：Ask 不会被中间件降级为 Allow。</summary>
    [Fact]
    public async Task ProductTool_WithoutAutoApprovalRule_IsHeldForApproval()
    {
        var (agent, client, executed) = BuildProductToolHarness(autoApprovalRules: []);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var response = await RunAsync(agent, session);

        response.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>()
            .Should().ContainSingle()
            .Which.ToolCall.Should().BeOfType<FunctionCallContent>()
            .Which.Name.Should().Be(ProductToolName);
        executed().Should().Be(0, "the tool must not run before the approval response");
        client.CallCount.Should().Be(1, "the model is called once; the call is withheld");
    }

    /// <summary>审批通过后工具恰好执行一次——审批响应绑定确实把批准送回了 FICC。</summary>
    [Fact]
    public async Task ApprovedResponse_ExecutesToolOnce()
    {
        var (agent, _, executed) = BuildProductToolHarness(autoApprovalRules: []);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var first = await RunAsync(agent, session);
        var request = SingleApprovalRequest(first);

        await RunAsync(agent, session, request.CreateResponse(approved: true));

        executed().Should().Be(1, "approval must lead to exactly one execution");
    }

    /// <summary>审批拒绝后工具不得执行（批准是执行的必要条件，而不是可选提示）。</summary>
    [Fact]
    public async Task DeniedResponse_DoesNotExecute()
    {
        var (agent, client, executed) = BuildProductToolHarness(autoApprovalRules: []);
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        var first = await RunAsync(agent, session);
        var request = SingleApprovalRequest(first);

        await RunAsync(agent, session, request.CreateResponse(approved: false, reason: "declined in test"));

        executed().Should().Be(0);
        LastToolResultText(client).Should().Contain("rejected", "the model must learn the call was rejected");
        client.CallCount.Should().Be(2, "FICC reports the rejection back to the model");
    }

    /// <summary>
    /// 无标记工具遇到 Ask 时中间件必须 fail-closed：这是「Ask 不放行未标记工具」的反证，
    /// 也是 ≤0 类缺陷（Ask 静默降级为 Allow）的守卫。
    /// </summary>
    [Fact]
    public async Task NeverModeTool_WithAskDecision_IsRefusedByMiddleware()
    {
        var executed = 0;
        var tool = AIFunctionFactory.Create(
            () =>
            {
                Interlocked.Increment(ref executed);
                return "scanned";
            },
            name: NeverModeToolName);
        var metadata = CreateMetadata((NeverModeToolName, ToolRisk.Destructive, ToolApprovalMode.Never));
        var client = new ScriptedChatClient(NeverModeToolName, Arguments());
        var (agent, _) = BuildHarness(
            client,
            enableTodo: false,
            autoApprovalRules: [],
            metadata: metadata,
            tools: [tool],
            askFor: [NeverModeToolName]);

        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        await RunAsync(agent, session);

        executed.Should().Be(0);
        LastToolResultText(client).Should().Contain("is not part of the approval protocol");
    }

    /// <summary>
    /// 待办工具名单是对 MAF 内部字面量的硬编码，必须与真正注入工具的那个提供器逐名一致。
    /// 名单漂移会让运行期标记与自动批准同时失效，且只在运行期才能发现。
    /// </summary>
    [Fact]
    public async Task TodoToolNames_MatchTheProviderThatInjectsThem()
    {
        var client = new ScriptedChatClient(ProviderToolName, Arguments());
        var (agent, _) = BuildHarness(client, enableTodo: true, autoApprovalRules: []);

        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var injected = await ToolNamesFromProviderAsync(agent, session);

        injected.Should().BeEquivalentTo(HarnessProviderTools.TodoToolNames,
            "HarnessProviderTools declares MAF's todo tool names as literals; a rename in the framework must fail here");
    }

    /// <summary>未挂载的提供器不得被登记，否则本地模型选路会看到不存在的工具。</summary>
    [Fact]
    public void RegisterMetadata_OnlyDeclaresMountedProviders()
    {
        var todoOnly = new ToolMetadataRegistry();
        HarnessProviderTools.RegisterMetadata(todoOnly, includeTodo: true, includeFileMemory: false);

        foreach (var name in HarnessProviderTools.TodoToolNames)
        {
            var policy = todoOnly.GetPolicy(name);
            policy.Risk.Should().Be(ToolRisk.Safe);
            policy.ApprovalMode.Should().NotBe(ToolApprovalMode.Never, "provider tools still cross the approval boundary");
        }

        todoOnly.Get(HarnessProviderTools.FileMemoryToolNames.First()).Should().BeNull(
            "an unmounted provider contributes no declared tools");
    }

    // Helpers

    private static IReadOnlyList<ToolApprovalRequestContent> ApprovalRequests(AgentResponse response) =>
        [.. response.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>()];

    private static ToolApprovalRequestContent SingleApprovalRequest(AgentResponse response) =>
        ApprovalRequests(response).Should().ContainSingle().Subject;

    private static string LastToolResultText(ScriptedChatClient client) =>
        string.Join(
            " | ",
            client.LastRequest
                .SelectMany(m => m.Contents)
                .OfType<FunctionResultContent>()
                .Select(result => result.Result?.ToString() ?? string.Empty));

    private static TodoProvider RequireTodoProvider(AIAgent agent) =>
        agent.GetService<TodoProvider>()
        ?? throw new InvalidOperationException("EnableTodo must resolve a TodoProvider through the agent chain.");

    private static async Task<IReadOnlyList<string>> ToolNamesFromProviderAsync(AIAgent agent, AgentSession session)
    {
        var provider = RequireTodoProvider(agent);
        var context = new AIContextProvider.InvokingContext(agent, session, new AIContext());
        var aiContext = await provider.InvokingAsync(context);
        return [.. aiContext.Tools!.OfType<AIFunction>().Select(tool => tool.Name)];
    }

    private static Task<AgentResponse> RunAsync(AIAgent agent, AgentSession session) =>
        agent.RunAsync("do the work", session, cancellationToken: TestContext.Current.CancellationToken);

    private static Task<AgentResponse> RunAsync(AIAgent agent, AgentSession session, AIContent response) =>
        agent.RunAsync(
            [new ChatMessage(ChatRole.User, [response])],
            session,
            cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>构建真实 Harness 管道，只有模型输出是脚本化的。</summary>
    private static (AIAgent Agent, ToolMetadataRegistry Metadata) BuildHarness(
        ScriptedChatClient client,
        bool enableTodo,
        IEnumerable<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>>? autoApprovalRules,
        ToolMetadataRegistry? metadata = null,
        IReadOnlyList<AITool>? tools = null,
        string[]? askFor = null)
    {
        var registry = metadata ?? new ToolMetadataRegistry();
        var handle = AgentPipelineBuilder.BuildHarnessAgent(new ChatClientAgentBuildOptions
        {
            ChatClient = client,
            Name = "approval-e2e",
            ChatOptions = new ChatOptions
            {
                ToolMode = ChatToolMode.Auto,
                // 生产链路总有产品工具；带上一个无害工具才会安装 MAF 的 function middleware，
                // 运行期注入的工具也要经同一条中间件链。
                Tools = [.. tools ?? [], BenignTool()],
            },
            LoggerFactory = NullLoggerFactory.Instance,
            ServiceProvider = new ServiceCollection().AddLogging().BuildServiceProvider(),
            ToolMetadata = registry,
            PipelineOptions = new AgentPipelineOptions
            {
                WorkingDirectory = Path.GetTempPath(),
                PermissionChecker = new AskOnlyChecker([.. askFor ?? [], ProviderToolName, ProductToolName]),
                AutoApprovalRules = autoApprovalRules,
                EnableTodo = enableTodo,
                EnableEditTransaction = false,
                EnableToolResultBudget = false,
                EnableStateMachine = false,
                EnableSafetyInvariants = false,
                EnableBehaviorContracts = false,
                EnableTaskRecovery = false,
            },
        });

        return (handle.Agent, registry);
    }

    /// <summary>产品工具审批往返用的管道：一个 Destructive/Always 桩工具。</summary>
    private static (AIAgent Agent, ScriptedChatClient Client, Func<int> Executed) BuildProductToolHarness(
        IEnumerable<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>>? autoApprovalRules)
    {
        var executed = 0;
        var tool = AIFunctionFactory.Create(
            () =>
            {
                Interlocked.Increment(ref executed);
                return "written";
            },
            name: ProductToolName);

        var client = new ScriptedChatClient(ProductToolName, Arguments());
        var (agent, _) = BuildHarness(
            client,
            enableTodo: false,
            autoApprovalRules: autoApprovalRules,
            metadata: CreateMetadata((ProductToolName, ToolRisk.Destructive, ToolApprovalMode.Always)),
            tools: [tool]);

        return (agent, client, () => Volatile.Read(ref executed));
    }

    private static ToolMetadataRegistry CreateMetadata(params (string Name, ToolRisk Risk, ToolApprovalMode Mode)[] entries)
    {
        var registry = new ToolMetadataRegistry();
        foreach (var (name, risk, mode) in entries)
            registry.Register(new ToolMetadata { Name = name, Risk = risk, ApprovalMode = mode });
        return registry;
    }

    private static AIFunction BenignTool() =>
        AIFunctionFactory.Create(() => "ok", name: BenignToolName);

    /// <summary>按 wire 形态（JsonElement）构造参数，与真实模型调用路径一致。</summary>
    private static Dictionary<string, object?> Arguments(object? payload = null)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (payload is null)
            return arguments;

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        foreach (var property in document.RootElement.EnumerateObject())
            arguments[property.Name] = property.Value.Clone();

        return arguments;
    }

    private static Dictionary<string, object?> TodosArguments(string title) =>
        Arguments(new { todos = new[] { new { title } } });

    /// <summary>对指定工具名返回 Ask，其余 Allow：让每个用例只关心自己的目标工具。</summary>
    private sealed class AskOnlyChecker(IReadOnlyList<string> askFor) : IPermissionChecker
    {
        public Task<PermissionCheckResult> CheckAsync(
            string toolName,
            JsonElement toolInput,
            ToolPermissionContext context,
            CancellationToken ct = default) =>
            Task.FromResult(
                askFor.Contains(toolName, StringComparer.Ordinal)
                    ? PermissionCheckResult.Ask($"{toolName} needs a human decision")
                    : PermissionCheckResult.Allow);
    }

    /// <summary>
    /// 首次请求报出一次目标工具调用，之后回普通回答，模拟真实模型的两轮交互。
    /// 同时留存最后一次请求，供断言工具结果文本。
    /// </summary>
    private sealed class ScriptedChatClient(string toolName, Dictionary<string, object?> arguments) : IChatClient
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public IReadOnlyList<ChatMessage> LastRequest { get; private set; } = [];

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastRequest = messages.ToList();
            var call = Interlocked.Increment(ref _callCount);
            return call == 1
                ? Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent("call-1", toolName, new Dictionary<string, object?>(arguments))])))
                : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "finished")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Messages[0].Contents);
        }
    }
}
