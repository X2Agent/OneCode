using OneCode.App.Tui;
using OneCode.Core.PlanMode;

namespace OneCode.App.Services.PlanMode;

public interface IPlanWorkflowApplicationService
{
    Task<PlanWorkflow?> GetAsync(SessionId sessionId, CancellationToken ct = default);

    /// <summary>
    /// 声明执行世代持有权：为聚合发放 fencing 令牌并返回刷新后的 Workflow。
    /// 幂等——令牌一经发放不再变更，同一执行世代（启动重试/恢复扫描）共享同一令牌；
    /// 新的执行请求以严格递增的新令牌重新 claim，旧世代的带令牌写被内核 fail-closed。
    /// </summary>
    Task<PlanWorkflow> ClaimExecutionAsync(
        SessionId sessionId,
        PlanWorkflowId planId,
        long expectedVersion,
        CancellationToken ct = default);

    Task<PlanSubmissionResult> SubmitAsync(SubmitPlanCommand command, CancellationToken ct = default);
    Task<PlanTransitionResult> ApproveAsync(ApprovePlanCommand command, CancellationToken ct = default);
    Task<PlanTransitionResult> RejectAsync(RejectPlanCommand command, CancellationToken ct = default);
    Task<PlanTransitionResult> RequestEditAsync(RequestPlanEditCommand command, CancellationToken ct = default);
    Task<PlanTransitionResult> CancelAsync(CancelPlanCommand command, CancellationToken ct = default);
    Task<PlanTransitionResult> RegisterStartAttemptAsync(RegisterPlanStartAttemptCommand command, CancellationToken ct = default);
    Task<PlanTransitionResult> BindBuildRunAsync(BindPlanBuildRunCommand command, CancellationToken ct = default);
    Task<PlanTransitionResult> FailExecutionRecoveryAsync(FailPlanExecutionRecoveryCommand command, CancellationToken ct = default);
    Task<PlanTransitionResult> UpdateStepAsync(UpdatePlanStepCommand command, CancellationToken ct = default);
    Task<PlanTransitionResult> CompleteExecutionAsync(CompletePlanExecutionCommand command, CancellationToken ct = default);
    Task<PlanTransitionResult> CompleteVerificationAsync(CompletePlanVerificationCommand command, CancellationToken ct = default);
    Task HandleRunEventAsync(PlanAgentRunEvent @event, CancellationToken ct = default);

    /// <summary>
    /// 断言当前会话存在可决策（<c>AwaitingApproval</c>）的 Plan 工作流并返回其投影；
    /// 工作流缺失或不可决策时抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    Task<PlanWorkflow> RequireDecidableAsync(SessionId sessionId, CancellationToken ct = default);

    /// <summary>
    /// 执行用户决策（批准/拒绝/请求修订）：构造并提交对应工作流命令，返回决策后的
    /// 工作流投影。幂等（重复 CommandId 返回既有状态），版本冲突/非法状态抛出（fail-closed）。
    /// </summary>
    Task<DecisionOutcome> DecideAsync(
        InteractiveSession session,
        PlanCardDecision decision,
        CancellationToken ct = default);
}

public sealed partial class PlanWorkflowApplicationService(IPlanAggregateStore aggregateStore)
    : IPlanWorkflowApplicationService
{
    public async Task<PlanWorkflow?> GetAsync(SessionId sessionId, CancellationToken ct = default)
        => (await aggregateStore.LoadAsync(sessionId, ct).ConfigureAwait(false))?.Workflow;

    public async Task<PlanWorkflow> ClaimExecutionAsync(
        SessionId sessionId,
        PlanWorkflowId planId,
        long expectedVersion,
        CancellationToken ct = default)
    {
        var aggregate = await RequireAggregateAsync(sessionId, planId, ct).ConfigureAwait(false);
        // 幂等：同一执行世代的重试与恢复扫描共享已发放的令牌，不再递增。
        if (aggregate.Workflow.WorkflowFencingToken is { } claimed)
            return aggregate.Workflow;

        var claimedAggregate = await aggregateStore.ClaimWorkflowAsync(
            sessionId,
            planId,
            DateTimeOffset.UtcNow.UtcTicks,
            expectedVersion,
            ct).ConfigureAwait(false);
        return claimedAggregate.Workflow;
    }

    public async Task<PlanSubmissionResult> SubmitAsync(
        SubmitPlanCommand command,
        CancellationToken ct = default)
    {
        PlanStepValidator.Validate(command.Steps);
        var existing = await aggregateStore.LoadAsync(command.SessionId, ct).ConfigureAwait(false);
        if (CommandIdempotency.IsReplay(existing?.Workflow, command.CommandId))
        {
            var duplicate = DuplicateRevisionResult(existing);
            return new PlanSubmissionResult(duplicate.Workflow, duplicate.Revision);
        }

        var current = existing?.Workflow ?? PlanWorkflow.Create(command.SessionId, command.ActiveRunId);
        PlanWorkflowValidator.ValidateExpectedVersion(existing?.Workflow, command.ExpectedWorkflowVersion);
        if (current.State != PlanWorkflowState.Planning)
            throw PlanWorkflowValidator.InvalidState(current, PlanWorkflowState.Planning);

        var revision = CreateRevision(
            current,
            command.Title,
            command.Markdown,
            command.Steps,
            command.Risks,
            command.Assumptions,
            PlanRevisionStatus.Submitted);
        var updated = current with
        {
            State = PlanWorkflowState.FinalizingPlanRun,
            LatestRevision = revision.Revision,
            SubmittedRevision = revision.Revision,
            ActiveRunId = command.ActiveRunId,
            ActiveRunKind = PlanRunKind.Planning,
            PendingFeedback = null,
            LastProcessedCommandId = command.CommandId,
            LastProcessedRevision = revision.Revision,
            Version = current.Version + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await aggregateStore.SaveAsync(
            new PlanAggregate(updated, [.. (existing?.Revisions ?? []), revision]),
            command.ExpectedWorkflowVersion,
            ct).ConfigureAwait(false);
        return new PlanSubmissionResult(updated, revision);
    }

    public Task<PlanTransitionResult> ApproveAsync(
        ApprovePlanCommand command,
        CancellationToken ct = default)
        => ExecuteAsync(async () =>
        {
            var aggregate = await RequireAggregateAsync(command.SessionId, command.PlanId, ct).ConfigureAwait(false);
            var current = aggregate.Workflow;
            if (CommandIdempotency.IsReplay(current, command.CommandId))
                return new PlanTransitionResult(current, IsDuplicateCommand: true);
            PlanWorkflowValidator.ValidateCommandIdentity(current, command.PlanId, command.Revision, command.ExpectedWorkflowVersion);
            if (current.State != PlanWorkflowState.AwaitingApproval)
                throw PlanWorkflowValidator.InvalidState(current, PlanWorkflowState.AwaitingApproval);

            var revision = aggregate.FindRevision(command.Revision)
                ?? throw new PlanTransitionException($"Plan revision {command.Revision} was not found.");
            if (revision.Status != PlanRevisionStatus.Submitted)
                throw new PlanTransitionException("Only a submitted revision can be approved.");

            var snapshot = FreezeApprovedSnapshot(revision, command.ApprovedBy);
            var updated = current with
            {
                State = PlanWorkflowState.StartingExecution,
                ApprovedRevision = command.Revision,
                ApprovedSnapshot = snapshot,
                ActiveRunId = null,
                ActiveRunKind = PlanRunKind.Build,
                ExecutionRequestId = command.CommandId,
                StartAttempt = 0,
                NextRetryAt = null,
                StepExecutions = snapshot.Steps.Select(step => new PlanStepExecution
                {
                    StepId = step.Id,
                    Status = PlanStepExecutionStatus.Pending,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }).ToArray(),
                LastProcessedCommandId = command.CommandId,
                Version = current.Version + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            var updatedRevisions = aggregate.Revisions
                .Select(candidate => candidate.Revision == command.Revision
                    ? candidate with { Status = PlanRevisionStatus.Approved }
                    : candidate)
                .ToArray();
            await aggregateStore.SaveAsync(
                new PlanAggregate(updated, updatedRevisions),
                current.Version,
                ct).ConfigureAwait(false);
            return new PlanTransitionResult(updated);
        });

    public Task<PlanTransitionResult> RejectAsync(
        RejectPlanCommand command,
        CancellationToken ct = default)
        => ApplyFeedbackAsync(
            command.CommandId,
            command.SessionId,
            command.PlanId,
            command.Revision,
            command.ExpectedWorkflowVersion,
            new PlanFeedback(
                PlanFeedbackKind.Rejected,
                command.Reason,
                [],
                command.Revision,
                DateTimeOffset.UtcNow),
            ct);

    public Task<PlanTransitionResult> RequestEditAsync(
        RequestPlanEditCommand command,
        CancellationToken ct = default)
        => ApplyFeedbackAsync(
            command.CommandId,
            command.SessionId,
            command.PlanId,
            command.Revision,
            command.ExpectedWorkflowVersion,
            new PlanFeedback(
                PlanFeedbackKind.EditRequested,
                command.Feedback,
                command.StepIds,
                command.Revision,
                DateTimeOffset.UtcNow),
            ct);

    public Task<PlanTransitionResult> CancelAsync(
        CancelPlanCommand command,
        CancellationToken ct = default)
        => ExecuteAsync(async () =>
        {
            var current = await RequireWorkflowAsync(command.SessionId, command.PlanId, ct).ConfigureAwait(false);
            if (CommandIdempotency.IsReplay(current, command.CommandId))
                return new PlanTransitionResult(current, IsDuplicateCommand: true);
            if (current.Version != command.ExpectedWorkflowVersion)
                throw new PlanConcurrencyException(
                    $"Plan workflow version conflict: expected {command.ExpectedWorkflowVersion}, actual {current.Version}.");
            if (current.State is PlanWorkflowState.Completed or PlanWorkflowState.Failed or PlanWorkflowState.Cancelled)
                throw new PlanTransitionException($"Plan workflow '{current.Id}' is already terminal in state '{current.State}'.");

            var executions = current.StepExecutions.Select(step => step.Status is
                    PlanStepExecutionStatus.Completed or PlanStepExecutionStatus.Failed or PlanStepExecutionStatus.Skipped
                ? step
                : step with
                {
                    Status = PlanStepExecutionStatus.Cancelled,
                    Error = command.Reason,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }).ToArray();
            var updated = current with
            {
                State = PlanWorkflowState.Cancelled,
                StepExecutions = executions,
                ActiveRunId = null,
                NextRetryAt = null,
                LastProcessedCommandId = command.CommandId,
                LastErrorCode = "Cancelled",
                LastErrorMessage = command.Reason,
                Version = current.Version + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            // 终态取消沿用当前世代令牌：claim 期间取消仍可落盘，而 claim 前的过期取消会被版本 CAS 拒绝。
            await SaveWorkflowAsync(updated, current.Version, current.WorkflowFencingToken ?? 0, ct).ConfigureAwait(false);
            return new PlanTransitionResult(updated);
        });

    public Task<PlanTransitionResult> RegisterStartAttemptAsync(
        RegisterPlanStartAttemptCommand command,
        CancellationToken ct = default)
        => ExecuteAsync(async () =>
        {
            var current = await RequireWorkflowAsync(command.SessionId, command.PlanId, ct).ConfigureAwait(false);
            if (CommandIdempotency.IsReplay(current, command.CommandId))
                return new PlanTransitionResult(current, IsDuplicateCommand: true);
            if (current.State != PlanWorkflowState.StartingExecution)
                throw PlanWorkflowValidator.InvalidState(current, PlanWorkflowState.StartingExecution);
            if (current.Version != command.ExpectedWorkflowVersion)
                throw new PlanConcurrencyException(
                    $"Plan workflow version conflict: expected {command.ExpectedWorkflowVersion}, actual {current.Version}.");

            var attempt = current.StartAttempt + 1;
            var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempt - 1, 5))));
            var updated = current with
            {
                StartAttempt = attempt,
                NextRetryAt = command.AttemptedAt + delay,
                LastProcessedCommandId = command.CommandId,
                Version = current.Version + 1,
                UpdatedAt = command.AttemptedAt,
            };
            await SaveWorkflowAsync(updated, current.Version, command.FencingToken, ct).ConfigureAwait(false);
            return new PlanTransitionResult(updated);
        });

    public Task<PlanTransitionResult> BindBuildRunAsync(
        BindPlanBuildRunCommand command,
        CancellationToken ct = default)
        => ExecuteAsync(async () =>
        {
            var current = await RequireWorkflowAsync(command.SessionId, command.PlanId, ct).ConfigureAwait(false);
            if (CommandIdempotency.IsReplay(current, command.CommandId))
                return new PlanTransitionResult(current, IsDuplicateCommand: true);
            if (current.State is not (PlanWorkflowState.Executing or PlanWorkflowState.Verifying))
                throw new PlanTransitionException(
                    $"Plan workflow '{current.Id}' cannot bind a BuildRun in state '{current.State}'.");
            if (!string.Equals(current.ActiveRunId, command.RunId, StringComparison.Ordinal))
                throw new PlanTransitionException(
                    $"Run '{command.RunId}' does not match active run '{current.ActiveRunId}'.");
            if (!string.IsNullOrWhiteSpace(current.BuildRunId)
                && !string.Equals(current.BuildRunId, command.BuildRunId, StringComparison.Ordinal))
            {
                throw new PlanTransitionException(
                    $"Plan workflow '{current.Id}' is already bound to BuildRun '{current.BuildRunId}'.");
            }
            if (string.Equals(current.BuildRunId, command.BuildRunId, StringComparison.Ordinal))
                return new PlanTransitionResult(current, IsDuplicateCommand: true);

            var updated = current with
            {
                BuildRunId = command.BuildRunId,
                LastProcessedCommandId = command.CommandId,
                Version = current.Version + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await SaveWorkflowAsync(updated, current.Version, command.FencingToken, ct).ConfigureAwait(false);
            return new PlanTransitionResult(updated);
        });

    public Task<PlanTransitionResult> FailExecutionRecoveryAsync(
        FailPlanExecutionRecoveryCommand command,
        CancellationToken ct = default)
        => ExecuteAsync(async () =>
        {
            var current = await RequireWorkflowAsync(command.SessionId, command.PlanId, ct).ConfigureAwait(false);
            if (CommandIdempotency.IsReplay(current, command.CommandId))
                return new PlanTransitionResult(current, IsDuplicateCommand: true);
            if (current.Version != command.ExpectedWorkflowVersion)
                throw new PlanConcurrencyException(
                    $"Plan workflow version conflict: expected {command.ExpectedWorkflowVersion}, actual {current.Version}.");
            if (current.State is not (PlanWorkflowState.Executing or PlanWorkflowState.Verifying))
                throw new PlanTransitionException(
                    $"Plan workflow '{current.Id}' cannot fail execution recovery from state '{current.State}'.");

            var updated = Failure(current, command.ErrorCode, command.ErrorMessage, command.FailedAt) with
            {
                LastProcessedCommandId = command.CommandId,
            };
            // 终态恢复失败沿用当前世代令牌（与取消同规则：claim 期间仍可落盘终态）。
            await SaveWorkflowAsync(updated, current.Version, current.WorkflowFencingToken ?? 0, ct).ConfigureAwait(false);
            return new PlanTransitionResult(updated);
        });

    public Task<PlanTransitionResult> UpdateStepAsync(
        UpdatePlanStepCommand command,
        CancellationToken ct = default)
        => ExecuteAsync(async () =>
        {
            var current = await RequireWorkflowAsync(command.SessionId, command.PlanId, ct).ConfigureAwait(false);
            if (CommandIdempotency.IsReplay(current, command.CommandId))
                return new PlanTransitionResult(current, IsDuplicateCommand: true);
            PlanWorkflowValidator.ValidateActiveBuildRun(current, command.RunId, PlanWorkflowState.Executing);

            var index = current.StepExecutions
                .Select((step, position) => (step, position))
                .FirstOrDefault(item => string.Equals(item.step.StepId, command.StepId, StringComparison.Ordinal));
            if (index.step is null)
                throw new PlanTransitionException($"Plan step '{command.StepId}' does not exist.");
            PlanWorkflowValidator.ValidateStepTransition(current, index.step, command);

            var executions = current.StepExecutions.ToArray();
            executions[index.position] = index.step with
            {
                Status = command.Status,
                Evidence = command.Evidence,
                Error = command.Error,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            var updated = current with
            {
                StepExecutions = executions,
                LastProcessedCommandId = command.CommandId,
                Version = current.Version + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await SaveWorkflowAsync(updated, current.Version, command.FencingToken, ct).ConfigureAwait(false);
            return new PlanTransitionResult(updated);
        });

    public Task<PlanTransitionResult> CompleteExecutionAsync(
        CompletePlanExecutionCommand command,
        CancellationToken ct = default)
        => ExecuteAsync(async () =>
        {
            var current = await RequireWorkflowAsync(command.SessionId, command.PlanId, ct).ConfigureAwait(false);
            if (CommandIdempotency.IsReplay(current, command.CommandId))
                return new PlanTransitionResult(current, IsDuplicateCommand: true);
            PlanWorkflowValidator.ValidateActiveBuildRun(current, command.RunId, PlanWorkflowState.Executing);
            var incomplete = current.StepExecutions
                .Where(step => step.Status is not (PlanStepExecutionStatus.Completed or PlanStepExecutionStatus.Skipped))
                .Select(step => step.StepId)
                .ToArray();
            if (incomplete.Length > 0)
                throw new PlanTransitionException($"Cannot verify while steps are incomplete: {string.Join(", ", incomplete)}.");
            if (current.StepExecutions.Any(step => step.Status == PlanStepExecutionStatus.Completed
                && string.IsNullOrWhiteSpace(step.Evidence)))
                throw new PlanTransitionException("Every completed step must include evidence.");

            var updated = current with
            {
                State = PlanWorkflowState.Verifying,
                CompletionSummary = command.Summary,
                LastProcessedCommandId = command.CommandId,
                Version = current.Version + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await SaveWorkflowAsync(updated, current.Version, command.FencingToken, ct).ConfigureAwait(false);
            return new PlanTransitionResult(updated);
        });

    public Task<PlanTransitionResult> CompleteVerificationAsync(
        CompletePlanVerificationCommand command,
        CancellationToken ct = default)
        => ExecuteAsync(async () =>
        {
            var current = await RequireWorkflowAsync(command.SessionId, command.PlanId, ct).ConfigureAwait(false);
            if (CommandIdempotency.IsReplay(current, command.CommandId))
                return new PlanTransitionResult(current, IsDuplicateCommand: true);
            PlanWorkflowValidator.ValidateActiveBuildRun(current, command.RunId, PlanWorkflowState.Verifying);
            if (command.Passed && command.Evidence.Count == 0)
                throw new PlanValidationException("Successful verification requires evidence.");

            var updated = command.Passed
                ? current with
                {
                    State = PlanWorkflowState.Completed,
                    VerificationEvidence = command.Evidence,
                    CompletionSummary = command.Summary,
                    ActiveRunId = null,
                    LastProcessedCommandId = command.CommandId,
                    Version = current.Version + 1,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }
                : Failure(current, "VerificationFailed", command.Summary, DateTimeOffset.UtcNow) with
                {
                    VerificationEvidence = command.Evidence,
                    LastProcessedCommandId = command.CommandId,
                };
            await SaveWorkflowAsync(updated, current.Version, command.FencingToken, ct).ConfigureAwait(false);
            return new PlanTransitionResult(updated);
        });

    public async Task HandleRunEventAsync(PlanAgentRunEvent @event, CancellationToken ct = default)
    {
        await ExecuteAsync(async () =>
        {
            var current = await RequireWorkflowAsync(@event.SessionId, @event.PlanId, ct).ConfigureAwait(false);
            var expectedRunId = current.State == PlanWorkflowState.StartingExecution
                && !string.IsNullOrWhiteSpace(current.ExecutionRequestId)
                    ? $"build-{current.ExecutionRequestId}"
                    : current.ActiveRunId;
            if (@event is not BuildRunStartedEvent
                && !string.Equals(expectedRunId, @event.RunId, StringComparison.Ordinal))
            {
                throw new PlanTransitionException(
                    $"Run '{@event.RunId}' does not match expected run '{expectedRunId}'.");
            }

            var updated = @event switch
            {
                PlanRunCompletedEvent { ProtocolValid: true }
                    when current.State == PlanWorkflowState.FinalizingPlanRun
                    => current with
                    {
                        State = PlanWorkflowState.AwaitingApproval,
                        ActiveRunId = null,
                        Version = current.Version + 1,
                        UpdatedAt = @event.OccurredAt,
                    },
                PlanRunCompletedEvent
                    => Failure(current, "ToolProtocolInvalid", "Plan run completed with an invalid tool protocol.", @event.OccurredAt),
                PlanRunFailedEvent failed
                    => Failure(current, failed.ErrorCode, failed.ErrorMessage, failed.OccurredAt),
                BuildRunStartedEvent when current.State == PlanWorkflowState.StartingExecution
                    => current with
                    {
                        State = PlanWorkflowState.Executing,
                        ActiveRunId = @event.RunId,
                        ActiveRunKind = PlanRunKind.Build,
                        Version = current.Version + 1,
                        UpdatedAt = @event.OccurredAt,
                    },
                BuildRunStartedEvent when current.State is PlanWorkflowState.Executing or PlanWorkflowState.Verifying
                    && string.Equals(current.ActiveRunId, @event.RunId, StringComparison.Ordinal)
                    => current,
                BuildRunFailedEvent failed when current.State is PlanWorkflowState.StartingExecution
                    or PlanWorkflowState.Executing
                    or PlanWorkflowState.Verifying
                    => Failure(current, failed.ErrorCode, failed.ErrorMessage, failed.OccurredAt),
                _ => throw new PlanTransitionException(
                    $"Event '{@event.GetType().Name}' is invalid in state '{current.State}'."),
            };

            await SaveWorkflowAsync(updated, current.Version, @event.FencingToken, ct).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    private Task<PlanTransitionResult> ApplyFeedbackAsync(
        string commandId,
        SessionId sessionId,
        PlanWorkflowId planId,
        int revision,
        long expectedVersion,
        PlanFeedback feedback,
        CancellationToken ct)
        => ExecuteAsync(async () =>
        {
            var current = await RequireWorkflowAsync(sessionId, planId, ct).ConfigureAwait(false);
            if (CommandIdempotency.IsReplay(current, commandId))
                return new PlanTransitionResult(current, IsDuplicateCommand: true);
            PlanWorkflowValidator.ValidateCommandIdentity(current, planId, revision, expectedVersion);
            if (current.State != PlanWorkflowState.AwaitingApproval)
                throw PlanWorkflowValidator.InvalidState(current, PlanWorkflowState.AwaitingApproval);

            var updated = current with
            {
                State = PlanWorkflowState.Planning,
                ActiveRunId = null,
                ActiveRunKind = PlanRunKind.Planning,
                PendingFeedback = feedback,
                LastProcessedCommandId = commandId,
                Version = current.Version + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            // 规划期反馈写恒未 claim，令牌为 0。
            await SaveWorkflowAsync(updated, current.Version, 0, ct).ConfigureAwait(false);
            return new PlanTransitionResult(updated);
        });


}
