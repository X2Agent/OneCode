using System.Runtime.CompilerServices;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Services.Compact;
using OneCode.App.Services.Coordinator;
using OneCode.App.Session;
using OneCode.App.Tools;
using OneCode.Core.Coordinator;
using OneCode.Core.Domain;
using OneCode.Core.Hooks;
using OneCode.Core.Models;
using OneCode.Core.Permissions;
using OneCode.Core.Prompt;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Api;
using OneCode.Tests.TestSupport;

namespace OneCode.Tests;

/// <summary>
/// §4.7 R4 端到端守卫：Team 成员产生的 MAF 工具审批请求必须被
/// <c>TeamWorkflowRunner</c> 的审批桥接到产品事件流，并在应答后让成员 turn 继续。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须端到端。</b> 审批桥的输入不是产品对象，而是 MAF 工作流抛出的
/// <c>RequestInfoEvent</c>（其 <c>Request</c> 负载是 <c>ToolApprovalRequestContent</c>）。
/// 该负载要经过 <c>FunctionInvokingChatClient</c> → <c>ToolApprovalAgent</c> →
/// <c>ChatClientAgent</c> → <c>AIAgentHostExecutor</c> →
/// <c>AIAgentUnservicedRequestsCollector</c> 五层才能出现在 <c>RequestInfoEvent</c> 上。
/// 任何一层改名、改包一层、改「不拦截用户输入」策略，桥的 <c>TryGetDataAs</c> 就会静默失配：
/// 成员 turn 永久挂起、工作流停摆，而单元测试仍然全绿。故本文件不 mock 任何一层，
/// 只替换最外层 <see cref="IChatClient"/>（脚本化的模型响应）与产品审批通道（<c>eventSink</c>）。
/// </para>
/// <para>
/// <b>正向断言</b>：审批请求真的浮出（工具名/参数正确）→ 批准 → 工具真的执行 → 运行收敛。
/// <b>反证</b>：拒绝 → 工具不得执行，且运行同样收敛（拒绝 ≠ 挂起）。
/// </para>
/// <para>
/// <b>AllowAlways 的覆盖意图。</b> 桥当前把 <c>AllowAlways</c> 显式降级为单次批准（standing rule
/// 包装能否无损通过工作流端口未验证，见 ADR 0007 §5.1）。用例把它与 <c>AllowOnce</c> 并列，
/// 钉住的正是「降级后仍然放行且不挂起」——降级允许，静默失配不允许。
/// </para>
/// </remarks>
public sealed class TeamToolApprovalBridgeTests
{
    private const string ToolName = "DangerousWrite";
    private const string HarnessFragment = "HARNESS_FRAGMENT_SENTINEL: sensitive-file protection";
    private const string MemberBody = "MEMBER_BODY_SENTINEL: execute exactly one task";

    /// <summary>
    /// 脚本化模型：第一次请求返回对审批工具的 <see cref="FunctionCallContent"/>，
    /// 之后返回纯文本终答。审批响应被框架注入后会再次请求模型，因此后续调用必须收敛。
    /// </summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            ChatMessage message = call == 1
                ? new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent("call-1", ToolName, new Dictionary<string, object?> { ["path"] = "target.txt" })])
                : new ChatMessage(ChatRole.Assistant, "task complete");
            return Task.FromResult(new ChatResponse(message) { FinishReason = ChatFinishReason.Stop });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            await Task.Yield();
            foreach (var content in response.Messages.SelectMany(m => m.Contents))
                yield return new ChatResponseUpdate(ChatRole.Assistant, [content]);
        }
    }

    private sealed record Harness(
        TeamWorkflowRunner Runner,
        ScriptedChatClient Client,
        ToolInvocationLog ToolLog,
        List<OrchestrationEvent.ApprovalRequest> ApprovalRequests);

    private sealed class ToolInvocationLog
    {
        private readonly List<string> _invocations = [];

        public IReadOnlyList<string> Invocations => _invocations;

        public void Record(string path) => _invocations.Add(path);
    }

    private static Harness CreateHarness(ApprovalDecision decision)
    {
        var client = new ScriptedChatClient();
        var toolLog = new ToolInvocationLog();
        var approvals = new List<OrchestrationEvent.ApprovalRequest>();

        var approvalTool = AIFunctionFactory.Create(
            (string path) =>
            {
                toolLog.Record(path);
                return $"wrote {path}";
            },
            ToolName,
            "Writes a file. Requires approval.");

        var metadata = new ToolMetadataRegistry();
        metadata.Register(new ToolMetadata
        {
            Name = ToolName,
            Risk = ToolRisk.Destructive,
            ApprovalMode = ToolApprovalMode.Always,
        });

        var promptManager = new PromptManager();
        promptManager.RegisterTemplate(new PromptTemplate(PromptComposer.HarnessPromptName, HarnessFragment));
        // 压缩策略在管道构建期加载 system/compact，缺失即 fail-fast。
        promptManager.RegisterTemplate(new PromptTemplate("system/compact", "COMPACT_PROMPT_SENTINEL"));

        var modeProvider = new PermissionModeProvider(TestConfigManager.Create());
        var modelManager = Substitute.For<IModelManager>();
        var (shared, main) = TestAgentContextProviderAssembly.Create(
            modelManager: modelManager,
            modeProvider: modeProvider,
            promptManager: promptManager);

        // Ask：既不自动放行（审批请求必须真的浮出），也不拒绝（拒绝在权限中间件层就短路了，
        // 永远走不到审批桥）。这正是 R4 要覆盖的分支。
        var permissionChecker = Substitute.For<IPermissionChecker>();
        permissionChecker
            .CheckAsync(
                Arg.Any<string>(),
                Arg.Any<System.Text.Json.JsonElement>(),
                Arg.Any<ToolPermissionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(PermissionCheckResult.Ask("test: requires approval")));

        var pipelineFactory = new SubAgentPipelineFactory(
            modeProvider,
            Substitute.For<IHookExecutionService>(),
            Substitute.For<IVerificationProvider>(),
            permissionChecker,
            Substitute.For<IAppStateAccessor>(),
            new TokenLedger(),
            TestConfigManager.Create());

        var agentFactory = new TeamAgentFactory(
            client,
            NullLoggerFactory.Instance,
            NullLogger<TeamAgentFactory>.Instance,
            Substitute.For<IServiceProvider>(),
            modelManager,
            promptManager,
            new PromptComposer(promptManager),
            new TeamAgentToolSources(
                Substitute.For<ICacheSafeParamsProvider>(),
                new ToolCatalog(new Lazy<List<AIFunction>>(() => [approvalTool]), metadata, null)),
            new TeamAgentPipelineDependencies(
                new AgentContextPipeline(shared, main),
                pipelineFactory,
                new CompactionStrategyFactory(
                    client,
                    modelManager,
                    new CompactPromptBuilder(promptManager)),
                Substitute.For<ISessionConversationAccess>(),
                metadata));

        var runner = new TeamWorkflowRunner(
            agentFactory,
            NullLogger<TeamWorkflowRunner>.Instance,
            executionEnvironment: InProcessExecution.Lockstep);

        return new Harness(runner, client, toolLog, approvals);
    }

    private static (TeamConfig Config, TeamTaskDefinition Task) CreateTeam()
    {
        var member = new TeamMember("executor-1", "executor", MemberBody);
        var config = new TeamConfig(
            TeamName: "approval-team",
            FilePath: "test.yaml",
            Members: [member],
            MaxTurns: 5,
            Mode: TeamOrchestrationMode.ParallelDag);
        var task = new TeamTaskDefinition(
            Id: "task-1",
            Title: "Write a file",
            Kind: TeamTaskKind.Implementation,
            AssigneeRole: "executor",
            DependsOn: [],
            AcceptanceCriteria: ["target.txt written"],
            ToolPolicy: TeamToolPolicy.WriteAllowed);
        return (config, task);
    }

    private static async Task<TeamRunResult> RunAsync(Harness harness, ApprovalDecision decision)
    {
        var (config, task) = CreateTeam();
        return await harness.Runner.RunTaskAsync(
            config,
            task,
            transaction: null!,
            cwd: Path.GetTempPath(),
            eventSink: evt =>
            {
                if (evt is OrchestrationEvent.ApprovalRequest request)
                {
                    harness.ApprovalRequests.Add(request);
                    request.ResponseSource.TrySetResult(decision);
                }
            },
            ct: TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(ApprovalDecision.AllowOnce)]
    [InlineData(ApprovalDecision.AllowAlways)]
    public async Task ApprovedToolCall_SurfacesApprovalRequest_ThenExecutesAndCompletes(ApprovalDecision decision)
    {
        var harness = CreateHarness(decision);

        var result = await RunAsync(harness, decision);

        harness.ApprovalRequests.Should().ContainSingle(
            "the member's tool approval request must surface to the product approval channel");
        harness.ApprovalRequests[0].Request.ToolName.Should().Be(ToolName,
            "the bridge maps the MAF ToolApprovalRequestContent back to the tool that triggered it");
        harness.ApprovalRequests[0].Request.ToolInput.Should().Contain("target.txt",
            "the approval card must carry the arguments the model asked for");

        harness.ToolLog.Invocations.Should().Equal(["target.txt"],
            "an approved call must actually execute — approval is not a no-op");
        harness.Client.Calls.Should().BeGreaterThan(1,
            "the member turn must resume after the approval response instead of stalling");
        result.Output.Should().Contain("task complete",
            "the member turn completes once the approval is answered");
    }

    [Fact]
    public async Task DeniedToolCall_SurfacesApprovalRequest_AndDoesNotExecute()
    {
        var harness = CreateHarness(ApprovalDecision.Deny);

        var result = await RunAsync(harness, ApprovalDecision.Deny);

        harness.ApprovalRequests.Should().ContainSingle(
            "a denial must still reach the product approval channel — the request may not be swallowed");
        harness.ToolLog.Invocations.Should().BeEmpty(
            "a denied call must never run");
        result.Output.Should().Contain("task complete",
            "a denial resumes the turn too; it must not stall the workflow");
    }

    [Fact]
    public async Task NoApprovalChannelConfigured_DoesNotStallTheWorkflow()
    {
        var harness = CreateHarness(ApprovalDecision.AllowOnce);
        var (config, task) = CreateTeam();

        var result = await harness.Runner.RunTaskAsync(
            config,
            task,
            transaction: null!,
            cwd: Path.GetTempPath(),
            eventSink: null,
            ct: TestContext.Current.CancellationToken);

        harness.ApprovalRequests.Should().BeEmpty(
            "without a product event sink there is no approval channel to bridge to");
        harness.ToolLog.Invocations.Should().BeEmpty(
            "the broker fails closed to Deny when no sink is available");
        result.Output.Should().Contain("task complete",
            "a fail-closed denial still resumes the turn — no silent stall");
    }
}
