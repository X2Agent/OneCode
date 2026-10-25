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
        if (existing is not null && CommandIdempotency.IsReplay(existing.Workflow, command.CommandId))
            return DuplicateRevisionResult(existing);

        var current = existing?.Workflow ?? PlanWorkflow.Create(command.SessionId, command.ActiveRunId);
        PlanWorkflowValidator.ValidateExpectedVersion(existing?.Workflow, command.ExpectedWorkflowVersion);

        // 幂等判据落在状态机而非工具层：模型在提交成功后重放同一份计划时，返回既有 revision
        // （语义「已提交、等待 run 收口」），而不是撞状态守卫把错误回灌给模型引发重试循环。
        // 内容哈希不同说明计划确实变了，需要新的规划轮次，仍按非法状态拒绝。
        if (current.State == PlanWorkflowState.FinalizingPlanRun
            && current.SubmittedRevision is { } submittedNumber
            && existing?.FindRevision(submittedNumber) is { } submitted
            && string.Equals(submitted.ContentHash, PlanContentHash.Of(command.Markdown), StringComparison.Ordinal))
        {
            return new PlanSubmissionResult(current, submitted);
        }

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

    public async Task<PlanTransitionResult> ApproveAsync(
        ApprovePlanCommand command,
        CancellationToken ct = default)
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

        var snapshot = FreezeApprovedSnapshot(revision, command);
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
    }

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

    public async Task<PlanTransitionResult> CancelAsync(
        CancelPlanCommand command,
        CancellationToken ct = default)
    {
        var current = await RequireWorkflowAsync(command.SessionId, command.PlanId, ct).ConfigureAwait(false);
        if (CommandIdempotency.IsReplay(current, command.CommandId))
            return new PlanTransitionResult(current, IsDuplicateCommand: true);
        if (current.Version != command.ExpectedWorkflowVersion)
            throw new PlanConcurrencyException(
                $"Plan workflow version conflict: expected {command.ExpectedWorkflowVersion}, actual {current.Version}.");
        if (current.State is PlanWorkflowState.Completed or PlanWorkflowState.Failed or PlanWorkflowState.Cancelled)
            throw new PlanTransitionException($"Plan workflow '{current.Id}' is already terminal in state '{current.State}'.");

        var updated = current with
        {
            State = PlanWorkflowState.Cancelled,
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
    }

    public async Task<PlanTransitionResult> RegisterStartAttemptAsync(
        RegisterPlanStartAttemptCommand command,
        CancellationToken ct = default)
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
    }

    public async Task<PlanTransitionResult> BindBuildRunAsync(
        BindPlanBuildRunCommand command,
        CancellationToken ct = default)
    {
        var current = await RequireWorkflowAsync(command.SessionId, command.PlanId, ct).ConfigureAwait(false);
        if (CommandIdempotency.IsReplay(current, command.CommandId))
            return new PlanTransitionResult(current, IsDuplicateCommand: true);
        // 同 run 重绑幂等先行：绑定关系未变化时任何状态都直接幂等返回
        // （收尾后 workflow 可能已终态，先过状态守卫会误报）；异 run 冲突仍拒绝。
        if (!string.IsNullOrWhiteSpace(current.BuildRunId)
            && string.Equals(current.BuildRunId, command.BuildRunId, StringComparison.Ordinal))
            return new PlanTransitionResult(current, IsDuplicateCommand: true);
        if (current.State != PlanWorkflowState.Executing)
            throw new PlanTransitionException(
                $"Plan workflow '{current.Id}' cannot bind a BuildRun in state '{current.State}'.");
        if (!string.Equals(current.ActiveRunId, command.RunId, StringComparison.Ordinal))
            throw new PlanTransitionException(
                $"Run '{command.RunId}' does not match active run '{current.ActiveRunId}'.");
        if (!string.IsNullOrWhiteSpace(current.BuildRunId))
        {
            throw new PlanTransitionException(
                $"Plan workflow '{current.Id}' is already bound to BuildRun '{current.BuildRunId}'.");
        }

        var updated = current with
        {
            BuildRunId = command.BuildRunId,
            LastProcessedCommandId = command.CommandId,
            Version = current.Version + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await SaveWorkflowAsync(updated, current.Version, command.FencingToken, ct).ConfigureAwait(false);
        return new PlanTransitionResult(updated);
    }

    public async Task<PlanTransitionResult> FailExecutionRecoveryAsync(
        FailPlanExecutionRecoveryCommand command,
        CancellationToken ct = default)
    {
        var current = await RequireWorkflowAsync(command.SessionId, command.PlanId, ct).ConfigureAwait(false);
        if (CommandIdempotency.IsReplay(current, command.CommandId))
            return new PlanTransitionResult(current, IsDuplicateCommand: true);
        if (current.Version != command.ExpectedWorkflowVersion)
            throw new PlanConcurrencyException(
                $"Plan workflow version conflict: expected {command.ExpectedWorkflowVersion}, actual {current.Version}.");
        if (current.State != PlanWorkflowState.Executing)
            throw new PlanTransitionException(
                $"Plan workflow '{current.Id}' cannot fail execution recovery from state '{current.State}'.");

        var updated = Failure(current, command.ErrorCode, command.ErrorMessage, command.FailedAt) with
        {
            LastProcessedCommandId = command.CommandId,
        };
        // 终态恢复失败沿用当前世代令牌（与取消同规则：claim 期间仍可落盘终态）。
        await SaveWorkflowAsync(updated, current.Version, current.WorkflowFencingToken ?? 0, ct).ConfigureAwait(false);
        return new PlanTransitionResult(updated);
    }

    public async Task HandleRunEventAsync(PlanAgentRunEvent @event, CancellationToken ct = default)
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
            BuildRunStartedEvent when current.State == PlanWorkflowState.Executing
                && string.Equals(current.ActiveRunId, @event.RunId, StringComparison.Ordinal)
                => current,
            // 成功由 BuildRun 终态驱动：run 正常结束 + 最终验证通过即计划完成，
            // 不再要求模型向工作流上报步骤与验证证据。
            BuildRunSucceededEvent when current.State == PlanWorkflowState.Executing
                => current with
                {
                    State = PlanWorkflowState.Completed,
                    ActiveRunId = null,
                    Version = current.Version + 1,
                    UpdatedAt = @event.OccurredAt,
                },
            BuildRunFailedEvent failed when current.State is PlanWorkflowState.StartingExecution
                or PlanWorkflowState.Executing
                => Failure(current, failed.ErrorCode, failed.ErrorMessage, failed.OccurredAt),
            _ => throw new PlanTransitionException(
                $"Event '{@event.GetType().Name}' is invalid in state '{current.State}'."),
        };

        if (ReferenceEquals(updated, current))
            return;

        await SaveWorkflowAsync(updated, current.Version, @event.FencingToken, ct).ConfigureAwait(false);
    }

    private async Task<PlanTransitionResult> ApplyFeedbackAsync(
        string commandId,
        SessionId sessionId,
        PlanWorkflowId planId,
        int revision,
        long expectedVersion,
        PlanFeedback feedback,
        CancellationToken ct)
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
    }


}
