using OneCode.App.Query;
using OneCode.App.Services.Compact;
using OneCode.App.Tui;
using OneCode.Core.Build;
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
/// boundary, Build mode switch, workflow events, and TUI event projection.
/// </summary>
public sealed class PlanAgentRunDispatcher(
    IPlanWorkflowApplicationService workflowService,
    PlanCardPublisher publisher,
    TuiInteractionBridge tui,
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
        if (workflow.State is not (PlanWorkflowState.Executing or PlanWorkflowState.Verifying)
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
            if (current.State is not (PlanWorkflowState.Executing or PlanWorkflowState.Verifying))
                return;
            ValidateRecoveryIdentity(current);
            await RunConversationAsync(session, current, ct).ConfigureAwait(false);
            return;
        }
        if (current.State is PlanWorkflowState.Executing or PlanWorkflowState.Verifying)
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
        if (current.StartAttempt >= 5)
        {
            await workflowService.HandleRunEventAsync(new BuildRunFailedEvent(
                current.SessionId,
                current.Id,
                $"build-{current.ExecutionRequestId}",
                "StartRetryExhausted",
                "Approved plan execution could not be started after five attempts.",
                DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
            return;
        }

        var attempt = await workflowService.RegisterStartAttemptAsync(
            new RegisterPlanStartAttemptCommand(
                $"start-{current.ExecutionRequestId}-{current.StartAttempt + 1}",
                current.SessionId,
                current.Id,
                current.Version,
                DateTimeOffset.UtcNow),
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
        var starting = current.State == PlanWorkflowState.StartingExecution;
        try
        {
            if (session.SessionManager.GetConversation(current.SessionId) is { } conversation)
            {
                MafSessionInvalidator.InvalidateRuntime(
                    conversation,
                    starting ? "approved-plan-build-boundary" : "approved-plan-build-recovery");
                await session.SessionManager.SaveAsync(ct).ConfigureAwait(false);
            }

            session.ModeController.Mode = WorkingMode.Build;
            if (starting)
            {
                await workflowService.HandleRunEventAsync(new BuildRunStartedEvent(
                    current.SessionId,
                    current.Id,
                    runId,
                    DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
                current = await workflowService.GetAsync(current.SessionId, ct).ConfigureAwait(false)
                    ?? throw new PlanTransitionException($"Plan '{current.Id}' disappeared after BuildRunStarted.");
            }

            publisher.Publish(current);
            tui.EmitEvent?.Invoke(new TuiWorkflowRunStarted(
                current.Id.ToString(),
                approvedSnapshot.Revision,
                approvedSnapshot.ContentHash));

            var request = new WorkflowRunRequest(
                runId,
                current.SessionId,
                BuildInstruction(approvedSnapshot, runId, current.State),
                session.SystemPrompt,
                session.Model,
                WorkingMode.Build,
                session.SessionManager.WorkingDirectory,
                ToBuildPlan(approvedSnapshot));

            await foreach (var queryEvent in session.ConversationRunner
                .StreamWorkflowRunAsync(request, ct).ConfigureAwait(false))
            {
                if (queryEvent is BuildRunStateEvent buildState)
                {
                    var binding = await workflowService.BindBuildRunAsync(new BindPlanBuildRunCommand(
                        $"bind-build-run-{current.Id}-{buildState.RunId}",
                        current.SessionId,
                        current.Id,
                        runId,
                        buildState.RunId.ToString()), ct).ConfigureAwait(false);
                    current = binding.Workflow;
                }
                if (TuiEventMapper.MapQueryEventToTuiEvent(queryEvent) is { } tuiEvent)
                    tui.EmitEvent?.Invoke(tuiEvent);
            }

            var terminal = await workflowService.GetAsync(current.SessionId, ct).ConfigureAwait(false);
            if (terminal?.State is PlanWorkflowState.Executing or PlanWorkflowState.Verifying)
            {
                await workflowService.HandleRunEventAsync(new BuildRunFailedEvent(
                    current.SessionId,
                    current.Id,
                    runId,
                    "ExecutionClosureMissing",
                    "Build run ended without completing plan steps and verification.",
                    DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
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
            if (latest?.State is PlanWorkflowState.Executing or PlanWorkflowState.Verifying
                || latest is { State: PlanWorkflowState.StartingExecution, StartAttempt: >= 5 })
            {
                await workflowService.HandleRunEventAsync(new BuildRunFailedEvent(
                    current.SessionId,
                    current.Id,
                    runId,
                    ex.GetType().Name,
                    ex.Message,
                    DateTimeOffset.UtcNow), CancellationToken.None).ConfigureAwait(false);
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
            tui.EmitEvent?.Invoke(new TuiError(
                latest?.State == PlanWorkflowState.StartingExecution
                    ? $"Approved plan start failed and will retry after {latest.NextRetryAt:u}: {ex.Message}"
                    : $"Approved plan execution failed: {ex.Message}"));
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

    private static BuildPlan ToBuildPlan(ApprovedPlanSnapshot snapshot)
        => new(
            $"Execute approved plan {snapshot.PlanId} revision {snapshot.Revision}.",
            snapshot.Steps.Select(step => new BuildPlanTask(
                step.Id,
                step.Title,
                step.Description,
                step.DependsOn,
                step.Files,
                step.AcceptanceCriteria)).ToArray(),
            [],
            [],
            [],
            RequireExplicitTaskCompletion: true);

    private static string BuildInstruction(
        ApprovedPlanSnapshot snapshot,
        string runId,
        PlanWorkflowState state)
    {
        var steps = string.Join("\n", snapshot.Steps.Select(step =>
            $"- {step.Id}: {step.Title}\n  {step.Description}\n  Acceptance: {string.Join("; ", step.AcceptanceCriteria)}"));
        var recoveryDirective = state == PlanWorkflowState.Verifying
            ? "The persisted workflow is already in Verifying. Do not modify implementation steps. Run only the required build/tests/checks and call CompletePlanVerification with concrete evidence."
            : "Execute steps in dependency order. Resume from the persisted step states; do not repeat completed steps.";
        return $"""
            Execute approved plan {snapshot.PlanId} revision {snapshot.Revision}.
            The approved snapshot is immutable; do not reinterpret or replace it.
            {recoveryDirective}

            ## Approved plan
            {snapshot.Markdown}

            ## Structured steps
            {steps}

            ## Required workflow protocol
            Active run id: {runId}
            1. Call UpdatePlanStep for every state change. Completed steps require concrete evidence.
            2. When all steps are completed or explicitly skipped, call CompletePlanExecution unless the workflow is already Verifying.
            3. Run the required build/tests/checks, then call CompletePlanVerification with command output as evidence.
            4. Do not claim completion unless CompletePlanVerification returns a completed workflow.
            """;
    }
}
