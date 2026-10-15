// PlanWorkflowApplicationServiceTests partial：Build 派工（启动 / 恢复 / 并发去重 / 重试耗尽）（原文件按职责区块的纯文本搬移）
using NSubstitute;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Services.PlanMode;
using OneCode.App.Session;
using OneCode.Core.Build;
using OneCode.Core.Domain;
using OneCode.Core.PlanMode;
namespace OneCode.Tests;

public sealed partial class PlanWorkflowApplicationServiceTests
{
    [Fact]
    public async Task Dispatcher_FutureRetry_DoesNotRegisterOrStartBuildRun()
    {
        var sessionId = SessionId.NewId();
        var workflow = CreateStartingWorkflow(sessionId, DateTimeOffset.UtcNow.AddMinutes(1));
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>()).Returns(workflow);
        var runner = Substitute.For<IConversationRunner>();
        var session = CreateInteractiveSession(Substitute.For<ISessionManager>(), runner);
        var sut = CreateDispatcher(workflowService);

        await sut.StartBuildAsync(session, workflow, TestContext.Current.CancellationToken);

        await workflowService.DidNotReceive().RegisterStartAttemptAsync(
            Arg.Any<RegisterPlanStartAttemptCommand>(),
            Arg.Any<CancellationToken>());
        await workflowService.DidNotReceive().HandleRunEventAsync(
            Arg.Any<BuildRunStartedEvent>(),
            Arg.Any<CancellationToken>());
        runner.DidNotReceive().StreamWorkflowRunAsync(
            Arg.Any<WorkflowRunRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispatcher_StartBuild_EmitsStartAndBindsObservedBuildRun()
    {
        var sessionId = SessionId.NewId();
        var workflow = CreateStartingWorkflow(sessionId, nextRetryAt: null);
        var attempted = workflow with
        {
            StartAttempt = 1,
            Version = workflow.Version + 1,
            NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(1),
        };
        var executing = attempted with
        {
            State = PlanWorkflowState.Executing,
            ActiveRunId = $"build-{workflow.ExecutionRequestId}",
        };
        var bound = executing with { BuildRunId = "br-observed", Version = executing.Version + 1 };
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.ClaimExecutionAsync(
                Arg.Any<SessionId>(),
                Arg.Any<PlanWorkflowId>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(workflow);
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(workflow, executing, bound, bound);
        workflowService.RegisterStartAttemptAsync(
                Arg.Any<RegisterPlanStartAttemptCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlanTransitionResult(attempted));
        workflowService.BindBuildRunAsync(
                Arg.Any<BindPlanBuildRunCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlanTransitionResult(bound));
        var runner = Substitute.For<IConversationRunner>();
        runner.StreamWorkflowRunAsync(Arg.Any<WorkflowRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleQueryEvent(new BuildRunStateEvent(
                new BuildRunId("br-observed"),
                BuildRunState.Implementing,
                1,
                [])));
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(_root);
        sessionManager.GetConversation(sessionId).Returns(CreateConversation(sessionId));
        var session = CreateInteractiveSession(sessionManager, runner);
        var sut = CreateDispatcher(workflowService);

        await sut.StartBuildAsync(session, workflow, TestContext.Current.CancellationToken);

        await workflowService.Received(1).HandleRunEventAsync(
            Arg.Is<BuildRunStartedEvent>(evt => evt.RunId == $"build-{workflow.ExecutionRequestId}"),
            TestContext.Current.CancellationToken);
        await workflowService.Received(1).BindBuildRunAsync(
            Arg.Is<BindPlanBuildRunCommand>(command =>
                command.RunId == $"build-{workflow.ExecutionRequestId}"
                && command.BuildRunId == "br-observed"),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 回归防护：计划批准发生在会话中途，<c>/model</c> 切换后的 BuildRun 必须使用
    /// 运行时模型（AppState 会话覆盖优先），而不是启动时写入会话的那份配置快照。
    /// </summary>
    [Fact]
    public async Task Dispatcher_StartBuild_UsesRuntimeModelNotConfiguredStartupSnapshot()
    {
        var sessionId = SessionId.NewId();
        var workflow = CreateStartingWorkflow(sessionId, nextRetryAt: null);
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.ClaimExecutionAsync(
                Arg.Any<SessionId>(),
                Arg.Any<PlanWorkflowId>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(workflow);
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>()).Returns(workflow);
        workflowService.RegisterStartAttemptAsync(
                Arg.Any<RegisterPlanStartAttemptCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlanTransitionResult(workflow with
            {
                StartAttempt = 1,
                Version = workflow.Version + 1,
            }));
        var runner = Substitute.For<IConversationRunner>();
        runner.StreamWorkflowRunAsync(Arg.Any<WorkflowRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyQueryEvents());
        var session = CreateInteractiveSession(Substitute.For<ISessionManager>(), runner);
        var appState = Substitute.For<IAppStateAccessor>();
        appState.Current.Returns(new AppState { MainLoopModel = "switched-model" });
        var sut = CreateDispatcher(workflowService, CreateModelManager("startup-model"), appState);

        await sut.StartBuildAsync(session, workflow, TestContext.Current.CancellationToken);

        runner.Received(1).StreamWorkflowRunAsync(
            Arg.Is<WorkflowRunRequest>(request => request.ModelId == "switched-model"),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Dispatcher_ResumeBuild_DoesNotEmitSecondBuildRunStartedEvent()
    {
        var sessionId = SessionId.NewId();
        var workflow = CreateExecutingWorkflow(sessionId, PlanWorkflowState.Executing);
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.ClaimExecutionAsync(
                Arg.Any<SessionId>(),
                Arg.Any<PlanWorkflowId>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(workflow);
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>()).Returns(workflow, workflow);
        var runner = Substitute.For<IConversationRunner>();
        runner.StreamWorkflowRunAsync(Arg.Any<WorkflowRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyQueryEvents());
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(_root);
        sessionManager.GetConversation(sessionId).Returns(CreateConversation(sessionId));
        var session = CreateInteractiveSession(sessionManager, runner);
        var sut = CreateDispatcher(workflowService);

        await sut.ResumeBuildAsync(session, workflow, TestContext.Current.CancellationToken);

        await workflowService.DidNotReceive().RegisterStartAttemptAsync(
            Arg.Any<RegisterPlanStartAttemptCommand>(),
            Arg.Any<CancellationToken>());
        await workflowService.DidNotReceive().HandleRunEventAsync(
            Arg.Any<BuildRunStartedEvent>(),
            Arg.Any<CancellationToken>());
        runner.Received(1).StreamWorkflowRunAsync(
            Arg.Any<WorkflowRunRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispatcher_ConcurrentSameExecutionRequest_RegistersSingleStartAttempt()
    {
        var sessionId = SessionId.NewId();
        var workflow = CreateStartingWorkflow(sessionId, nextRetryAt: null);
        var attemptRegistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.ClaimExecutionAsync(
                Arg.Any<SessionId>(),
                Arg.Any<PlanWorkflowId>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(workflow);
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>()).Returns(workflow);
        workflowService.RegisterStartAttemptAsync(
                Arg.Any<RegisterPlanStartAttemptCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                attemptRegistered.SetResult();
                await releaseAttempt.Task;
                return new PlanTransitionResult(workflow with
                {
                    StartAttempt = 1,
                    Version = workflow.Version + 1,
                    NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(1),
                });
            });
        var runner = Substitute.For<IConversationRunner>();
        runner.StreamWorkflowRunAsync(Arg.Any<WorkflowRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyQueryEvents());
        var session = CreateInteractiveSession(Substitute.For<ISessionManager>(), runner);
        var sut = CreateDispatcher(workflowService);

        var first = sut.StartBuildAsync(session, workflow, TestContext.Current.CancellationToken);
        await attemptRegistered.Task;
        var second = sut.StartBuildAsync(session, workflow, TestContext.Current.CancellationToken);
        releaseAttempt.SetResult();
        await Task.WhenAll(first, second);

        await workflowService.Received(1).RegisterStartAttemptAsync(
            Arg.Any<RegisterPlanStartAttemptCommand>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispatcher_ConcurrentRecovery_StreamsSingleBuildRun()
    {
        var sessionId = SessionId.NewId();
        var workflow = CreateExecutingWorkflow(sessionId, PlanWorkflowState.Executing);
        var releaseRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.ClaimExecutionAsync(
                Arg.Any<SessionId>(),
                Arg.Any<PlanWorkflowId>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(workflow);
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(workflow, workflow, workflow);
        var runner = Substitute.For<IConversationRunner>();
        runner.StreamWorkflowRunAsync(Arg.Any<WorkflowRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => BlockingQueryEvents(runStarted, releaseRun));
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(_root);
        sessionManager.GetConversation(sessionId).Returns(CreateConversation(sessionId));
        var session = CreateInteractiveSession(sessionManager, runner);
        var sut = CreateDispatcher(workflowService);

        var first = sut.ResumeBuildAsync(session, workflow, TestContext.Current.CancellationToken);
        await runStarted.Task;
        var second = sut.ResumeBuildAsync(session, workflow, TestContext.Current.CancellationToken);
        releaseRun.SetResult();
        await Task.WhenAll(first, second);

        runner.Received(1).StreamWorkflowRunAsync(
            Arg.Any<WorkflowRunRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispatcher_PassesApprovedStepsAsPrescribedBuildPlan()
    {
        var sessionId = SessionId.NewId();
        var workflow = CreateStartingWorkflow(sessionId, nextRetryAt: null);
        var attempted = workflow with
        {
            StartAttempt = 1,
            Version = workflow.Version + 1,
            NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(1),
        };
        var executing = attempted with { State = PlanWorkflowState.Executing };
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.ClaimExecutionAsync(
                Arg.Any<SessionId>(),
                Arg.Any<PlanWorkflowId>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(workflow);
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(workflow, executing, executing);
        workflowService.RegisterStartAttemptAsync(
                Arg.Any<RegisterPlanStartAttemptCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlanTransitionResult(attempted));
        var runner = Substitute.For<IConversationRunner>();
        WorkflowRunRequest? captured = null;
        runner.StreamWorkflowRunAsync(
                Arg.Do<WorkflowRunRequest>(request => captured = request),
                Arg.Any<CancellationToken>())
            .Returns(EmptyQueryEvents());
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(_root);
        sessionManager.GetConversation(sessionId).Returns(CreateConversation(sessionId));
        var session = CreateInteractiveSession(sessionManager, runner);
        var sut = CreateDispatcher(workflowService);

        await sut.StartBuildAsync(session, workflow, TestContext.Current.CancellationToken);

        captured.Should().NotBeNull();
        captured!.PrescribedBuildPlan.Should().NotBeNull();
        captured.PrescribedBuildPlan!.Tasks.Should().BeEquivalentTo(
            workflow.ApprovedSnapshot!.Steps.Select(step => new BuildPlanTask(
                step.Id,
                step.Title,
                step.Description,
                step.DependsOn,
                step.Files,
                step.AcceptanceCriteria)));
    }

    [Fact]
    public async Task Dispatcher_StartFailureBeforeBuildRun_PreservesStartingExecutionForRetry()
    {
        var sessionId = SessionId.NewId();
        var workflow = CreateStartingWorkflow(sessionId, nextRetryAt: null);
        var attempted = workflow with
        {
            StartAttempt = 1,
            Version = workflow.Version + 1,
            NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(1),
        };
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.ClaimExecutionAsync(
                Arg.Any<SessionId>(),
                Arg.Any<PlanWorkflowId>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(workflow);
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(workflow, attempted);
        workflowService.RegisterStartAttemptAsync(
                Arg.Any<RegisterPlanStartAttemptCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlanTransitionResult(attempted));
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.GetConversation(sessionId).Returns(CreateConversation(sessionId));
        sessionManager.SaveAsync(Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new IOException("persistence unavailable"));
        var session = CreateInteractiveSession(sessionManager);
        var sut = CreateDispatcher(workflowService);

        await sut.StartBuildAsync(session, workflow, TestContext.Current.CancellationToken);

        attempted.State.Should().Be(PlanWorkflowState.StartingExecution);
        attempted.NextRetryAt.Should().NotBeNull();
        await workflowService.DidNotReceive().HandleRunEventAsync(
            Arg.Any<BuildRunFailedEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispatcher_FifthAttemptExhausted_TransitionsWorkflowToFailed()
    {
        var sessionId = SessionId.NewId();
        var workflow = CreateStartingWorkflow(sessionId, nextRetryAt: null) with { StartAttempt = 5 };
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.ClaimExecutionAsync(
                Arg.Any<SessionId>(),
                Arg.Any<PlanWorkflowId>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(workflow);
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>()).Returns(workflow);
        var session = CreateInteractiveSession(Substitute.For<ISessionManager>());
        var sut = CreateDispatcher(workflowService);

        await sut.StartBuildAsync(session, workflow, TestContext.Current.CancellationToken);

        await workflowService.Received(1).HandleRunEventAsync(
            Arg.Is<BuildRunFailedEvent>(evt => evt.ErrorCode == "StartRetryExhausted"),
            TestContext.Current.CancellationToken);
        await workflowService.DidNotReceive().RegisterStartAttemptAsync(
            Arg.Any<RegisterPlanStartAttemptCommand>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(BuildRunState.Completed, null)]
    [InlineData(BuildRunState.Blocked, "BuildRunPendingReview")]
    [InlineData(BuildRunState.Failed, "BuildRunFailed")]
    [InlineData(BuildRunState.Implementing, "BuildRunIncomplete")]
    public async Task Dispatcher_StreamEnds_ClosesWorkflowFromFinalBuildRunState(
        BuildRunState finalState,
        string? expectedFailureCode)
    {
        // 计划成败只看 BuildRun 终态：Completed → 成功；Blocked（产物保留待复核）与 Failed 各有专属错误码；
        // 流在非终态结束（例如被中断）按未完成收口，绝不静默当成功。
        var (workflow, executing, bound) = CreateExecutingTriple(SessionId.NewId());
        var workflowService = CreateClosureWorkflowService(workflow, executing, bound);
        var session = CreateClosureSession(workflow, finalState, workflowService, out var sut);

        await sut.StartBuildAsync(session, workflow, TestContext.Current.CancellationToken);

        if (expectedFailureCode is null)
        {
            await workflowService.Received(1).HandleRunEventAsync(
                Arg.Is<PlanAgentRunEvent>(evt => evt is BuildRunSucceededEvent),
                TestContext.Current.CancellationToken);
            await workflowService.DidNotReceive().HandleRunEventAsync(
                Arg.Is<PlanAgentRunEvent>(evt => evt is BuildRunFailedEvent),
                Arg.Any<CancellationToken>());
        }
        else
        {
            await workflowService.Received(1).HandleRunEventAsync(
                Arg.Is<PlanAgentRunEvent>(evt => evt is BuildRunFailedEvent
                    && ((BuildRunFailedEvent)evt).ErrorCode == expectedFailureCode),
                TestContext.Current.CancellationToken);
            await workflowService.DidNotReceive().HandleRunEventAsync(
                Arg.Is<PlanAgentRunEvent>(evt => evt is BuildRunSucceededEvent),
                Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task Dispatcher_WorkflowCancelledDuringStream_AppendsNoClosureEvent()
    {
        // 流期间用户取消了工作流（终态）：收口事件会被状态机拒绝，不得再追加。
        var sessionId = SessionId.NewId();
        var (workflow, executing, bound) = CreateExecutingTriple(sessionId);
        var cancelled = bound with { State = PlanWorkflowState.Cancelled, ActiveRunId = null };
        var workflowService = CreateClosureWorkflowService(workflow, executing, bound);
        workflowService.GetAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(workflow, executing, cancelled);
        var session = CreateClosureSession(workflow, BuildRunState.Completed, workflowService, out var sut);

        await sut.StartBuildAsync(session, workflow, TestContext.Current.CancellationToken);

        await workflowService.DidNotReceive().HandleRunEventAsync(
            Arg.Is<PlanAgentRunEvent>(evt => evt is BuildRunSucceededEvent || evt is BuildRunFailedEvent),
            Arg.Any<CancellationToken>());
    }

    private (PlanWorkflow Starting, PlanWorkflow Executing, PlanWorkflow Bound) CreateExecutingTriple(SessionId sessionId)
    {
        var workflow = CreateStartingWorkflow(sessionId, nextRetryAt: null);
        var executing = workflow with
        {
            State = PlanWorkflowState.Executing,
            StartAttempt = 1,
            Version = workflow.Version + 1,
            ActiveRunId = $"build-{workflow.ExecutionRequestId}",
        };
        var bound = executing with { BuildRunId = "br-observed", Version = executing.Version + 1 };
        return (workflow, executing, bound);
    }

    private static IPlanWorkflowApplicationService CreateClosureWorkflowService(
        PlanWorkflow workflow,
        PlanWorkflow executing,
        PlanWorkflow bound)
    {
        var workflowService = Substitute.For<IPlanWorkflowApplicationService>();
        workflowService.ClaimExecutionAsync(
                Arg.Any<SessionId>(),
                Arg.Any<PlanWorkflowId>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(workflow);
        workflowService.GetAsync(workflow.SessionId, Arg.Any<CancellationToken>())
            .Returns(workflow, executing, bound);
        workflowService.RegisterStartAttemptAsync(
                Arg.Any<RegisterPlanStartAttemptCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlanTransitionResult(workflow with { StartAttempt = 1, Version = workflow.Version + 1 }));
        workflowService.BindBuildRunAsync(
                Arg.Any<BindPlanBuildRunCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new PlanTransitionResult(bound));
        return workflowService;
    }

    private InteractiveSession CreateClosureSession(
        PlanWorkflow workflow,
        BuildRunState finalState,
        IPlanWorkflowApplicationService workflowService,
        out PlanAgentRunDispatcher dispatcher)
    {
        var runner = Substitute.For<IConversationRunner>();
        runner.StreamWorkflowRunAsync(Arg.Any<WorkflowRunRequest>(), Arg.Any<CancellationToken>())
            .Returns(SingleQueryEvent(new BuildRunStateEvent(
                new BuildRunId("br-observed"),
                finalState,
                1,
                [])));
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(_root);
        sessionManager.GetConversation(workflow.SessionId).Returns(CreateConversation(workflow.SessionId));
        dispatcher = CreateDispatcher(workflowService);
        return CreateInteractiveSession(sessionManager, runner);
    }}
