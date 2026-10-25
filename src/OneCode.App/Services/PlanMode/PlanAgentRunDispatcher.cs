using OneCode.App.Query;
using OneCode.App.Services.Agent;
using OneCode.App.Services.Compact;
using OneCode.Core.Build;
using OneCode.Core.Models;
using OneCode.Core.PlanMode;

namespace OneCode.App.Services.PlanMode;

public interface IPlanAgentRunDispatcher
{
    Task StartBuildAsync(
        InteractiveSession session,
        PlanWorkflow workflow,
        CancellationToken ct = default);

    Task ResumeBuildAsync(
        InteractiveSession session,
        PlanWorkflow workflow,
        CancellationToken ct = default);
}

/// <summary>
/// Application-level dispatcher for approved-plan execution. It owns the fresh agent-run
/// boundary, Build mode switch, workflow events, and run-event projection to frontends
/// (via <see cref="RunEventBridge"/>).
/// </summary>
public sealed class PlanAgentRunDispatcher(
    IPlanWorkflowApplicationService workflowService,
    PlanCardPublisher publisher,
    OneCode.App.Services.RunEventBridge runEvents,
    IModelManager modelManager,
    IAppStateAccessor appState,
    ILogger<PlanAgentRunDispatcher> logger)
    : IPlanAgentRunDispatcher
{
    private readonly ConcurrentDictionary<string, Lazy<Task>> _activeStarts = new(StringComparer.Ordinal);

    public Task StartBuildAsync(
        InteractiveSession session,
        PlanWorkflow workflow,
        CancellationToken ct = default)
    {
        if (workflow.State != PlanWorkflowState.StartingExecution
            || workflow.ApprovedSnapshot is null
            || string.IsNullOrWhiteSpace(workflow.ExecutionRequestId))
        {
            throw new PlanTransitionException(
                $"Plan '{workflow.Id}' is not ready to start execution from state '{workflow.State}'.");
        }

        return DispatchAsync(
            workflow.ExecutionRequestId,
            () => RunBuildAsync(session, workflow, isRecovery: false, ct));
    }

    public Task ResumeBuildAsync(
        InteractiveSession session,
        PlanWorkflow workflow,
        CancellationToken ct = default)
    {
        if (workflow.State != PlanWorkflowState.Executing
            || workflow.ApprovedSnapshot is null
            || string.IsNullOrWhiteSpace(workflow.ExecutionRequestId)
            || string.IsNullOrWhiteSpace(workflow.ActiveRunId))
        {
            throw new PlanTransitionException(
                $"Plan '{workflow.Id}' is not recoverable from state '{workflow.State}'.");
        }

        var expectedRunId = $"build-{workflow.ExecutionRequestId}";
        if (!string.Equals(workflow.ActiveRunId, expectedRunId, StringComparison.Ordinal))
        {
            throw new PlanTransitionException(
                $"Plan '{workflow.Id}' active run '{workflow.ActiveRunId}' does not match execution request '{expectedRunId}'.");
        }

        return DispatchAsync(
            workflow.ExecutionRequestId,
            () => RunBuildAsync(session, workflow, isRecovery: true, ct));
    }

    private Task DispatchAsync(string key, Func<Task> action)
    {
        var lazy = _activeStarts.GetOrAdd(
            key,
            _ => new Lazy<Task>(action, LazyThreadSafetyMode.ExecutionAndPublication));
        return AwaitAndReleaseAsync(key, lazy);
    }

    private async Task AwaitAndReleaseAsync(string key, Lazy<Task> lazy)
    {
        try
        {
            await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            _activeStarts.TryRemove(new KeyValuePair<string, Lazy<Task>>(key, lazy));
        }
    }

    private async Task RunBuildAsync(
        InteractiveSession session,
        PlanWorkflow workflow,
        bool isRecovery,
        CancellationToken ct)
    {
        var current = await workflowService.GetAsync(workflow.SessionId, ct).ConfigureAwait(false);
        if (current is null)
            throw new PlanTransitionException($"Plan '{workflow.Id}' no longer exists.");
        if (current.State == PlanWorkflowState.Completed)
            return;
        if (isRecovery)
        {
            if (current.State != PlanWorkflowState.Executing)
                return;
            ValidateRecoveryIdentity(current);
            // 执行世代入口 claim（幂等）：恢复路径的所有后续持久化都携带世代令牌。
            current = await workflowService.ClaimExecutionAsync(
                current.SessionId,
                current.Id,
                current.Version,
                ct).ConfigureAwait(false);
            await RunConversationAsync(session, current, ct).ConfigureAwait(false);
            return;
        }
        if (current.State == PlanWorkflowState.Executing)
            return;
        if (current.State != PlanWorkflowState.StartingExecution || current.ApprovedSnapshot is null)
            throw new PlanTransitionException($"Plan '{current.Id}' cannot start from '{current.State}'.");
        if (current.NextRetryAt is { } retryAt && retryAt > DateTimeOffset.UtcNow)
        {
            logger.LogInformation(
                "Plan {PlanId} start attempt {Attempt} is deferred until {RetryAt}",
                current.Id,
                current.StartAttempt,
                retryAt);
            return;
        }
        // 执行世代入口 claim（幂等）：启动路径在重试簿记之前发放令牌，
        // 其后所有执行期写（重试簿记、事件、绑定）都携带世代令牌。
        current = await workflowService.ClaimExecutionAsync(
            current.SessionId,
            current.Id,
            current.Version,
            ct).ConfigureAwait(false);
        var fencingToken = current.WorkflowFencingToken ?? 0;
        if (current.StartAttempt >= 5)
        {
            await workflowService.HandleRunEventAsync(new BuildRunFailedEvent(
                current.SessionId,
                current.Id,
                $"build-{current.ExecutionRequestId}",
                "StartRetryExhausted",
                "Approved plan execution could not be started after five attempts.",
                DateTimeOffset.UtcNow,
                fencingToken), ct).ConfigureAwait(false);
            return;
        }

        var attempt = await workflowService.RegisterStartAttemptAsync(
            new RegisterPlanStartAttemptCommand(
                $"start-{current.ExecutionRequestId}-{current.StartAttempt + 1}",
                current.SessionId,
                current.Id,
                current.Version,
                DateTimeOffset.UtcNow,
                fencingToken),
            ct).ConfigureAwait(false);
        current = attempt.Workflow;
        await RunConversationAsync(session, current, ct).ConfigureAwait(false);
    }

    private async Task RunConversationAsync(
        InteractiveSession session,
        PlanWorkflow current,
        CancellationToken ct)
    {
        var approvedSnapshot = current.ApprovedSnapshot
            ?? throw new PlanTransitionException(
                $"Plan '{current.Id}' lost its approved snapshot while starting or recovering execution.");
        var runId = $"build-{current.ExecutionRequestId}";
        var fencingToken = current.WorkflowFencingToken ?? 0;
        var starting = current.State == PlanWorkflowState.StartingExecution;
        try
        {
            // 恢复路径不丢弃 MAF 会话：上次 attempt 已持久化的会话同时携带本执行世代的历史与
            // 待办进度，丢弃会让已勾选的步骤全部归零重做。只有新世代首次启动才重建 MAF 运行时会话。
            if (starting && session.SessionManager.GetConversation(current.SessionId) is { } conversation)
            {
                MafSessionInvalidator.InvalidateRuntime(conversation, "approved-plan-build-boundary");
                if (current.ExecutionRequestId is { } executionRequestId)
                {
                    // 执行世代历史播种边界：首次启动时记录转录位置，受控 run 的历史播种据此
                    // 剥离边界之前的 SubmitPlan 调用并保留规划期只读检索结果；同世代重试沿用不回移。
                    HistorySeedBoundary.RecordForExecution(
                        conversation,
                        executionRequestId,
                        session.SessionManager.GetChatHistory(current.SessionId).Count);
                }
                await session.SessionManager.SaveAsync(ct).ConfigureAwait(false);
            }

            session.ModeController.Mode = WorkingMode.Build;
            if (starting)
            {
                await workflowService.HandleRunEventAsync(new BuildRunStartedEvent(
                    current.SessionId,
                    current.Id,
                    runId,
                    DateTimeOffset.UtcNow,
                    fencingToken), ct).ConfigureAwait(false);
                current = await workflowService.GetAsync(current.SessionId, ct).ConfigureAwait(false)
                    ?? throw new PlanTransitionException($"Plan '{current.Id}' disappeared after BuildRunStarted.");
            }

            publisher.Publish(current);
            runEvents.Publish(new WorkflowRunStartedEvent(
                current.Id.ToString(),
                approvedSnapshot.Revision,
                approvedSnapshot.ContentHash));

            // 模型取运行时真相源（AppState 会话覆盖 > 配置有效值），与普通查询派工
            // （QueryStreamService）同源：计划批准发生在会话中途，/model 切换后
            // 这条 BuildRun 必须用新模型，不得使用会话启动时的快照。
            var modelId = modelManager.GetMainModel(appState.Current.MainLoopModel).Id;

            var request = new WorkflowRunRequest(
                runId,
                current.SessionId,
                PlanExecutionProtocol.BuildTriggerInstruction(approvedSnapshot),
                session.SystemPrompt,
                modelId,
                WorkingMode.Build,
                session.SessionManager.WorkingDirectory,
                approvedSnapshot.Project(),
                HarnessInstructions: session.HarnessPrompt);

            // 绑定只需一次：BuildRunStateEvent 会在执行中多次投影（Implementing、Verifying 等），
            // 重复调用 BindBuildRunAsync 会被单槽幂等标记放行为新命令，且收尾事件到达时
            // plan workflow 可能已 Completed——首绑成功后跳过后续绑定。
            var buildRunBound = false;
            BuildRunStateEvent? lastBuildState = null;
            await foreach (var queryEvent in session.ConversationRunner
                .StreamWorkflowRunAsync(request, ct).ConfigureAwait(false))
            {
                if (queryEvent is BuildRunStateEvent buildState)
                {
                    lastBuildState = buildState;
                    if (!buildRunBound)
                    {
                        var binding = await workflowService.BindBuildRunAsync(new BindPlanBuildRunCommand(
                            $"bind-build-run-{current.Id}-{buildState.RunId}",
                            current.SessionId,
                            current.Id,
                            runId,
                            buildState.RunId.ToString(),
                            fencingToken), ct).ConfigureAwait(false);
                        current = binding.Workflow;
                        buildRunBound = true;
                    }
                }
                runEvents.Publish(queryEvent);
            }

            // 计划成败只看 BuildRun 终态：Completed = run 正常结束且最终验证通过。
            // 流期间用户可能已取消工作流（终态），此时不再追加收口事件。
            var terminal = await workflowService.GetAsync(current.SessionId, ct).ConfigureAwait(false);
            if (terminal?.State == PlanWorkflowState.Executing)
            {
                await workflowService.HandleRunEventAsync(
                    ClosureEvent(current, runId, lastBuildState, fencingToken), ct).ConfigureAwait(false);
                terminal = await workflowService.GetAsync(current.SessionId, ct).ConfigureAwait(false);
            }

            if (terminal is not null)
                publisher.Publish(terminal);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Approved plan Build run failed for session {SessionId}, plan {PlanId}, run {RunId}",
                current.SessionId,
                current.Id,
                runId);
            var latest = await workflowService.GetAsync(current.SessionId, CancellationToken.None).ConfigureAwait(false);
            if (latest?.State is PlanWorkflowState.Executing
                || latest is { State: PlanWorkflowState.StartingExecution, StartAttempt: >= 5 })
            {
                await workflowService.HandleRunEventAsync(new BuildRunFailedEvent(
                    current.SessionId,
                    current.Id,
                    runId,
                    ex.GetType().Name,
                    ex.Message,
                    DateTimeOffset.UtcNow,
                    fencingToken), CancellationToken.None).ConfigureAwait(false);
                latest = await workflowService.GetAsync(current.SessionId, CancellationToken.None).ConfigureAwait(false);
            }
            else if (latest?.State == PlanWorkflowState.StartingExecution)
            {
                logger.LogWarning(
                    "Plan {PlanId} remains in StartingExecution for persisted retry attempt {Attempt} at {RetryAt}",
                    latest.Id,
                    latest.StartAttempt,
                    latest.NextRetryAt);
            }
            if (latest is not null)
                publisher.Publish(latest);
            runEvents.Publish(new ErrorEvent(
                latest?.State == PlanWorkflowState.StartingExecution
                    ? $"Approved plan start failed and will retry after {latest.NextRetryAt:u}: {ex.Message}"
                    : $"Approved plan execution failed: {ex.Message}"));
        }
        finally
        {
            // 与 WorkflowRunStartedEvent 配对：无论成功、失败还是异常收尾都要复位前端的
            // 忙碌/流式状态，否则 run 结束后界面会一直停留在"等待模型响应"。
            runEvents.Publish(new WorkflowRunEndedEvent(current.Id.ToString()));
        }
    }

    private static void ValidateRecoveryIdentity(PlanWorkflow workflow)
    {
        var expectedRunId = $"build-{workflow.ExecutionRequestId}";
        if (!string.Equals(workflow.ActiveRunId, expectedRunId, StringComparison.Ordinal))
        {
            throw new PlanTransitionException(
                $"Plan '{workflow.Id}' active run '{workflow.ActiveRunId}' does not match execution request '{expectedRunId}'.");
        }
    }

    /// <summary>流末收口事件：终态由最后一次 BuildRun 状态投影决定；没有任何投影按未完成计。</summary>
    private static PlanAgentRunEvent ClosureEvent(
        PlanWorkflow workflow,
        string runId,
        BuildRunStateEvent? lastBuildState,
        long fencingToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (lastBuildState?.State == BuildRunState.Completed)
            return new BuildRunSucceededEvent(workflow.SessionId, workflow.Id, runId, now, fencingToken);

        var state = lastBuildState?.State ?? BuildRunState.Created;
        return new BuildRunFailedEvent(
            workflow.SessionId,
            workflow.Id,
            runId,
            BuildRunClosure.FailureCode(state),
            BuildRunClosure.FailureMessage(state, lastBuildState?.FailureSummary),
            now,
            fencingToken);
    }
}
