// QueryStreamEngineTests partial：Plan 桥接（计划提交 → awaiting approval）
using OneCode.Core.Config;
using System.Threading.Channels;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Services.Notifier;
using OneCode.App.Services.Observability;
using OneCode.App.Services.PlanMode;
using OneCode.App.Services.Streaming;
using OneCode.App.Services.Compact;
using OneCode.App.Session;
using OneCode.App.Tools;
using OneCode.Core.Coordinator;
using OneCode.Core.Domain;
using OneCode.Core.Hooks;
using OneCode.Core.PlanMode;
using OneCode.Core.Tools;
using OneCode.Tests.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
namespace OneCode.Tests;

public sealed partial class QueryStreamEngineTests
{
    /// <summary>
    /// Plan-mode interactive run bridge: SubmitPlan (executed inside the agent run)
    /// persists FinalizingPlanRun; when the run stream completes, the engine must
    /// transition the workflow to AwaitingApproval and publish it — this is the
    /// event that pops the approval InlineSelector in the TUI.
    /// </summary>
    [Fact]
    public async Task StreamQueryAsync_PlanRunWithSubmittedPlan_TransitionsToAwaitingApprovalOnCompletion()
    {
        string? capturedRunId = null;
        bool? submitFailed = null;

        var outcome = await RunPlanModeAsync(async createPlanTool =>
        {
            // Simulate the LLM calling SubmitPlan inside the agent run —
            // OneCodeAgentRunContext.CurrentRunId must be set by the engine here.
            capturedRunId = OneCodeAgentRunContext.CurrentRunId;
            var result = await createPlanTool.SubmitPlanAsync(CreatePlanBridgeMarkdown(), [CreatePlanBridgeStep()]);
            submitFailed = result.IsError;
            return new MainAgentRunResult(Text: "done", TotalInputTokens: 0, TotalOutputTokens: 0, TurnCount: 1);
        });

        capturedRunId.Should().NotBeNull("SubmitPlan must execute inside the engine's activation context");
        submitFailed.Should().BeFalse("SubmitPlan should succeed in plan mode with a valid plan");
        outcome.Workflow!.State.Should().Be(PlanWorkflowState.AwaitingApproval,
            "run completion must open approval (FinalizingPlanRun → AwaitingApproval)");
        outcome.PublishedStates.Should().Contain(PlanWorkflowState.AwaitingApproval,
            "the AwaitingApproval workflow must be published so the TUI pops the approval selector");
    }

    /// <summary>
    /// 提交成功即落封：同一 run 内 SubmitPlan 之后，中间件探针必须看到封条（工具调用据此被拒）。
    /// </summary>
    [Fact]
    public async Task StreamQueryAsync_PlanRunSubmitSucceeds_SealsTheRunForLaterToolCalls()
    {
        bool? sealedBefore = null;
        bool? sealedAfter = null;

        await RunPlanModeAsync(async createPlanTool =>
        {
            sealedBefore = OneCodeAgentRunContext.PlanSubmissionSeal?.IsSealed;
            await createPlanTool.SubmitPlanAsync(CreatePlanBridgeMarkdown(), [CreatePlanBridgeStep()]);
            sealedAfter = OneCodeAgentRunContext.PlanSubmissionSeal?.IsSealed;
            return new MainAgentRunResult(Text: "done", TotalInputTokens: 0, TotalOutputTokens: 0, TurnCount: 1);
        });

        sealedBefore.Should().BeFalse("每个 Plan run 起步时持有一枚未落封的封条");
        sealedAfter.Should().BeTrue("SubmitPlan 成功后封条必须对同一 run 的其余工具调用可见");
    }

    /// <summary>
    /// 计划提交之后 run 以异常收场：计划已落盘，审批入口不得因收尾失败而丢失。
    /// </summary>
    [Fact]
    public async Task StreamQueryAsync_PlanRunFailsAfterSubmit_StillOpensApproval()
    {
        var outcome = await RunPlanModeAsync(async createPlanTool =>
        {
            await createPlanTool.SubmitPlanAsync(CreatePlanBridgeMarkdown(), [CreatePlanBridgeStep()]);
            throw new InvalidOperationException("model stream broke after the plan was saved");
        });

        outcome.Workflow!.State.Should().Be(PlanWorkflowState.AwaitingApproval,
            "异常终态也必须经收口漏斗，否则已提交的计划永远停在 FinalizingPlanRun");
        outcome.PublishedStates.Should().Contain(PlanWorkflowState.AwaitingApproval);
    }

    /// <summary>
    /// 输出 hook 拦截最终响应（早返回分支）：同样必须经收口漏斗。
    /// </summary>
    [Fact]
    public async Task StreamQueryAsync_PlanRunOutputBlockedByHook_StillOpensApproval()
    {
        var hook = Substitute.For<IHookExecutionService>();
        var blocked = new AggregatedHookResult { BlockingErrors = [new HookBlockingError("blocked-by-policy", "policy.sh")] };

        var outcome = await RunPlanModeAsync(
            async createPlanTool =>
            {
                await createPlanTool.SubmitPlanAsync(CreatePlanBridgeMarkdown(), [CreatePlanBridgeStep()]);
                return new MainAgentRunResult(Text: "done", TotalInputTokens: 0, TotalOutputTokens: 0, TurnCount: 1);
            },
            hook,
            blocked);

        outcome.Workflow!.State.Should().Be(PlanWorkflowState.AwaitingApproval,
            "hook 拦截分支 yield break 之前必须收口");
    }

    private sealed record PlanBridgeOutcome(PlanWorkflow? Workflow, IReadOnlyList<PlanWorkflowState> PublishedStates);

    /// <summary>
    /// 以真实工作流存储 + 真实 SubmitPlan 工具驱动一次 Plan 模式引擎 run；<paramref name="runBody"/>
    /// 扮演 agent run（在引擎设置好的 ambient 上下文内执行）。
    /// </summary>
    private async Task<PlanBridgeOutcome> RunPlanModeAsync(
        Func<CreatePlanTool, Task<MainAgentRunResult>> runBody,
        IHookExecutionService? hookService = null,
        AggregatedHookResult? outputHookResult = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "onecode-plan-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new PlanAggregateStore(root);
            var workflowService = new PlanWorkflowApplicationService(store);
            var bus = new OrchestrationEventBus();
            var publisher = new PlanCardPublisher(bus);
            var publishedStates = new List<PlanWorkflowState>();
            bus.Subscribe(evt =>
            {
                if (evt is OrchestrationEvent.PlanProjectionChanged changed)
                    publishedStates.Add(changed.Workflow.State);
            });

            var conversation = new Conversation { Id = SessionId.NewId(), WorkingDirectory = root };
            var sessionManager = Substitute.For<ISessionManager>();
            sessionManager.ForegroundConversation.Returns(conversation);
            sessionManager.WorkingDirectory.Returns(root);
            sessionManager.GetChatHistory(conversation.Id).Returns(new List<ChatMessage>());

            var planMode = Substitute.For<IPlanModeService>();
            planMode.IsInPlanMode.Returns(true);
            var createPlanTool = new CreatePlanTool(planMode, publisher, sessionManager, workflowService, store);

            var runner = Substitute.For<IMainAgentRunner>();
            runner.RunStreamingAsync(
                    Arg.Any<MainAgentRunOptions>(),
                    Arg.Do<ChannelWriter<object>>(writer =>
                    {
                        writer.TryWrite(new AgentResponseUpdate { Contents = { new TextContent("计划卡片已就绪") } });
                        writer.Complete();
                    }),
                    Arg.Any<CancellationToken>())
                .Returns(_ => runBody(createPlanTool));

            var toolMetadata = new ToolMetadataRegistry();
            var toolCatalog = new ToolCatalog(new Lazy<List<AIFunction>>(() => []), toolMetadata, mcpConnectionManager: null);
            var configManager = TestConfigManager.Create();
            configManager.Current.Returns(ConfigSnapshot.FromEffective(new AppSettings()));
            var hooks = hookService ?? Substitute.For<IHookExecutionService>();
            if (outputHookResult is not null)
            {
                hooks.FireAsync(Arg.Is<HookPayload>(p => p.Point == HookInterceptionPoint.Output), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(outputHookResult);
            }

            var sut = new QueryStreamEngine(
                Substitute.For<ILogger<QueryStreamEngine>>(),
                runner,
                toolCatalog,
                hooks,
                new ChatSessionDependencies(
                    sessionManager,
                    new SessionToolSetManager(toolCatalog, toolMetadata),
                    new ToolCapabilityResolver(toolCatalog),
                    configManager,
                    PlanWorkflow: workflowService,
                    PlanCardPublisher: publisher),
                new ChatObservabilityDependencies(
                    Substitute.For<ITokenUsageTracker>(),
                    new TokenBreakdownEstimator(TestSupport.TestTokenEstimators.Default),
                    Substitute.For<INotifierService>()),
                planRunCloser: new PlanRunCloser(sessionManager, new ToolProtocolValidator(), workflowService, publisher));

            await foreach (var _ in sut.StreamQueryAsync(
                "请规划重构方案", "system", "model1",
                ct: TestContext.Current.CancellationToken,
                workingMode: WorkingMode.Plan))
            {
            }

            var workflow = await workflowService.GetAsync(conversation.Id, TestContext.Current.CancellationToken);
            return new PlanBridgeOutcome(workflow, publishedStates);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
