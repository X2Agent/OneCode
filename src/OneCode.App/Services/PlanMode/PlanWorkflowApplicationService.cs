using System.Security.Cryptography;
using System.Text;
using OneCode.Core.PlanMode;

namespace OneCode.App.Services.PlanMode;

public interface IPlanWorkflowApplicationService
{
    Task<PlanWorkflow?> GetAsync(SessionId sessionId, CancellationToken ct = default);
    Task<PlanRevisionResult> SaveDraftAsync(SavePlanDraftCommand command, CancellationToken ct = default);
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
}

public sealed class PlanWorkflowApplicationService(IPlanAggregateStore aggregateStore)
    : IPlanWorkflowApplicationService
{
    public async Task<PlanWorkflow?> GetAsync(SessionId sessionId, CancellationToken ct = default)
        => (await aggregateStore.LoadAsync(sessionId, ct).ConfigureAwait(false))?.Workflow;

    public async Task<PlanRevisionResult> SaveDraftAsync(
        SavePlanDraftCommand command,
        CancellationToken ct = default)
    {
        PlanStepValidator.Validate(command.Steps);
        var existing = await aggregateStore.LoadAsync(command.SessionId, ct).ConfigureAwait(false);
        if (existing?.Workflow.LastProcessedCommandId == command.CommandId)
            return DuplicateRevisionResult(existing);

        var current = existing?.Workflow;
        ValidateExpectedVersion(current, command.ExpectedWorkflowVersion);
        if (current is not null && current.State != PlanWorkflowState.Planning)
            throw InvalidState(current, PlanWorkflowState.Planning);

        var workflow = current ?? PlanWorkflow.Create(command.SessionId, command.ActiveRunId);
        var revision = CreateRevision(
            workflow,
            command.Title,
            command.Markdown,
            command.Steps,
            command.Risks,
            command.Assumptions,
            PlanRevisionStatus.Draft);
        var updated = workflow with
        {
            LatestRevision = revision.Revision,
            ActiveRunId = command.ActiveRunId ?? workflow.ActiveRunId,
            ActiveRunKind = PlanRunKind.Planning,
            LastProcessedCommandId = command.CommandId,
            LastProcessedRevision = revision.Revision,
            Version = workflow.Version + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await aggregateStore.SaveAsync(
            new PlanAggregate(updated, [.. (existing?.Revisions ?? []), revision]),
            command.ExpectedWorkflowVersion,
            ct).ConfigureAwait(false);
        return new PlanRevisionResult(updated, revision);
    }

    public async Task<PlanSubmissionResult> SubmitAsync(
        SubmitPlanCommand command,
        CancellationToken ct = default)
    {
        PlanStepValidator.Validate(command.Steps);
        var existing = await aggregateStore.LoadAsync(command.SessionId, ct).ConfigureAwait(false);
        if (existing?.Workflow.LastProcessedCommandId == command.CommandId)
        {
            var duplicate = DuplicateRevisionResult(existing);
            return new PlanSubmissionResult(duplicate.Workflow, duplicate.Revision);
        }

        var current = existing?.Workflow ?? PlanWorkflow.Create(command.SessionId, command.ActiveRunId);
        ValidateExpectedVersion(existing?.Workflow, command.ExpectedWorkflowVersion);
        if (current.State != PlanWorkflowState.Planning)
            throw InvalidState(current, PlanWorkflowState.Planning);

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
            if (current.LastProcessedCommandId == command.CommandId)
                return new PlanTransitionResult(current, IsDuplicateCommand: true);
            ValidateCommandIdentity(current, command.PlanId, command.Revision, command.ExpectedWorkflowVersion);
            if (current.State != PlanWorkflowState.AwaitingApproval)
                throw InvalidState(current, PlanWorkflowState.AwaitingApproval);

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
            if (current.LastProcessedCommandId == command.CommandId)
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
            await SaveWorkflowAsync(updated, current.Version, ct).ConfigureAwait(false);
            return new PlanTransitionResult(updated);
        });

    public Task<PlanTransitionResult> RegisterStartAttemptAsync(
        RegisterPlanStartAttemptCommand command,
        CancellationToken ct = default)
        => ExecuteAsync(async () =>
        {
            var current = await RequireWorkflowAsync(command.SessionId, command.PlanId, ct).ConfigureAwait(false);
            if (current.LastProcessedCommandId == command.CommandId)
                return new PlanTransitionResult(current, IsDuplicateCommand: true);
            if (current.State != PlanWorkflowState.StartingExecution)
                throw InvalidState(current, PlanWorkflowState.StartingExecution);
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
            await SaveWorkflowAsync(updated, current.Version, ct).ConfigureAwait(false);
            return new PlanTransitionResult(updated);
        });

    public Task<PlanTransitionResult> BindBuildRunAsync(
        BindPlanBuildRunCommand command,
        CancellationToken ct = default)
        => ExecuteAsync(async () =>
        {
            var current = await RequireWorkflowAsync(command.SessionId, command.PlanId, ct).ConfigureAwait(false);
            if (current.LastProcessedCommandId == command.CommandId)
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
            await SaveWorkflowAsync(updated, current.Version, ct).ConfigureAwait(false);
            return new PlanTransitionResult(updated);
        });

    public Task<PlanTransitionResult> FailExecutionRecoveryAsync(
        FailPlanExecutionRecoveryCommand command,
        CancellationToken ct = default)
        => ExecuteAsync(async () =>
        {
            var current = await RequireWorkflowAsync(command.SessionId, command.PlanId, ct).ConfigureAwait(false);
            if (current.LastProcessedCommandId == command.CommandId)
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
            await SaveWorkflowAsync(updated, current.Version, ct).ConfigureAwait(false);
            return new PlanTransitionResult(updated);
        });

    public Task<PlanTransitionResult> UpdateStepAsync(
        UpdatePlanStepCommand command,
        CancellationToken ct = default)
        => ExecuteAsync(async () =>
        {
            var current = await RequireWorkflowAsync(command.SessionId, command.PlanId, ct).ConfigureAwait(false);
            if (current.LastProcessedCommandId == command.CommandId)
                return new PlanTransitionResult(current, IsDuplicateCommand: true);
            ValidateActiveBuildRun(current, command.RunId, PlanWorkflowState.Executing);

            var index = current.StepExecutions
                .Select((step, position) => (step, position))
                .FirstOrDefault(item => string.Equals(item.step.StepId, command.StepId, StringComparison.Ordinal));
            if (index.step is null)
                throw new PlanTransitionException($"Plan step '{command.StepId}' does not exist.");
            ValidateStepTransition(current, index.step, command);

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
            await SaveWorkflowAsync(updated, current.Version, ct).ConfigureAwait(false);
            return new PlanTransitionResult(updated);
        });

    public Task<PlanTransitionResult> CompleteExecutionAsync(
        CompletePlanExecutionCommand command,
        CancellationToken ct = default)
        => ExecuteAsync(async () =>
        {
            var current = await RequireWorkflowAsync(command.SessionId, command.PlanId, ct).ConfigureAwait(false);
            if (current.LastProcessedCommandId == command.CommandId)
                return new PlanTransitionResult(current, IsDuplicateCommand: true);
            ValidateActiveBuildRun(current, command.RunId, PlanWorkflowState.Executing);
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
            await SaveWorkflowAsync(updated, current.Version, ct).ConfigureAwait(false);
            return new PlanTransitionResult(updated);
        });

    public Task<PlanTransitionResult> CompleteVerificationAsync(
        CompletePlanVerificationCommand command,
        CancellationToken ct = default)
        => ExecuteAsync(async () =>
        {
            var current = await RequireWorkflowAsync(command.SessionId, command.PlanId, ct).ConfigureAwait(false);
            if (current.LastProcessedCommandId == command.CommandId)
                return new PlanTransitionResult(current, IsDuplicateCommand: true);
            ValidateActiveBuildRun(current, command.RunId, PlanWorkflowState.Verifying);
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
            await SaveWorkflowAsync(updated, current.Version, ct).ConfigureAwait(false);
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

            await SaveWorkflowAsync(updated, current.Version, ct).ConfigureAwait(false);
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
            if (current.LastProcessedCommandId == commandId)
                return new PlanTransitionResult(current, IsDuplicateCommand: true);
            ValidateCommandIdentity(current, planId, revision, expectedVersion);
            if (current.State != PlanWorkflowState.AwaitingApproval)
                throw InvalidState(current, PlanWorkflowState.AwaitingApproval);

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
            await SaveWorkflowAsync(updated, current.Version, ct).ConfigureAwait(false);
            return new PlanTransitionResult(updated);
        });

    private async Task<PlanWorkflow> RequireWorkflowAsync(
        SessionId sessionId,
        PlanWorkflowId planId,
        CancellationToken ct)
        => (await RequireAggregateAsync(sessionId, planId, ct).ConfigureAwait(false)).Workflow;

    private async Task<PlanAggregate> RequireAggregateAsync(
        SessionId sessionId,
        PlanWorkflowId planId,
        CancellationToken ct)
    {
        var aggregate = await aggregateStore.LoadAsync(sessionId, ct).ConfigureAwait(false)
            ?? throw new PlanTransitionException($"No active plan workflow exists for session '{sessionId}'.");
        if (aggregate.Workflow.Id != planId)
            throw new PlanTransitionException($"Plan '{planId}' does not belong to session '{sessionId}'.");
        return aggregate;
    }

    private async Task SaveWorkflowAsync(PlanWorkflow workflow, long expectedVersion, CancellationToken ct)
    {
        var aggregate = await RequireAggregateAsync(workflow.SessionId, workflow.Id, ct).ConfigureAwait(false);
        await aggregateStore.SaveAsync(
            aggregate with { Workflow = workflow },
            expectedVersion,
            ct).ConfigureAwait(false);
    }

    private static void ValidateCommandIdentity(
        PlanWorkflow workflow,
        PlanWorkflowId planId,
        int revision,
        long expectedVersion)
    {
        if (workflow.Id != planId)
            throw new PlanTransitionException("Plan ID does not match the active workflow.");
        if (workflow.SubmittedRevision != revision)
            throw new PlanTransitionException(
                $"Revision {revision} is not the submitted revision {workflow.SubmittedRevision}.");
        if (workflow.Version != expectedVersion)
            throw new PlanConcurrencyException(
                $"Plan workflow version conflict: expected {expectedVersion}, actual {workflow.Version}.");
    }

    private static PlanRevision CreateRevision(
        PlanWorkflow workflow,
        string title,
        string markdown,
        IReadOnlyList<PlanStepDefinition> steps,
        IReadOnlyList<string> risks,
        IReadOnlyList<string> assumptions,
        PlanRevisionStatus status)
        => new()
        {
            PlanId = workflow.Id,
            SessionId = workflow.SessionId,
            Revision = workflow.LatestRevision + 1,
            Title = title,
            Markdown = markdown,
            Steps = steps,
            Risks = risks,
            Assumptions = assumptions,
            ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(markdown))).ToLowerInvariant(),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static void ValidateActiveBuildRun(
        PlanWorkflow workflow,
        string runId,
        PlanWorkflowState expectedState)
    {
        if (workflow.State != expectedState)
            throw InvalidState(workflow, expectedState);
        if (!string.Equals(workflow.ActiveRunId, runId, StringComparison.Ordinal))
            throw new PlanTransitionException(
                $"Run '{runId}' does not match active run '{workflow.ActiveRunId}'.");
    }

    private static void ValidateStepTransition(
        PlanWorkflow workflow,
        PlanStepExecution currentStep,
        UpdatePlanStepCommand command)
    {
        if (command.Status == PlanStepExecutionStatus.Completed && string.IsNullOrWhiteSpace(command.Evidence))
            throw new PlanValidationException($"Completed step '{command.StepId}' requires evidence.");
        if (command.Status == PlanStepExecutionStatus.Failed && string.IsNullOrWhiteSpace(command.Error))
            throw new PlanValidationException($"Failed step '{command.StepId}' requires an error.");
        if (currentStep.Status is PlanStepExecutionStatus.Completed
            or PlanStepExecutionStatus.Failed
            or PlanStepExecutionStatus.Skipped
            or PlanStepExecutionStatus.Cancelled)
        {
            if (currentStep.Status != command.Status)
            {
                throw new PlanTransitionException(
                    $"Plan step '{command.StepId}' is terminal in state '{currentStep.Status}' and cannot transition to '{command.Status}'.");
            }

            return;
        }
        if (currentStep.Status == PlanStepExecutionStatus.Pending
            && command.Status is not (PlanStepExecutionStatus.InProgress
                or PlanStepExecutionStatus.Failed
                or PlanStepExecutionStatus.Skipped))
        {
            throw new PlanTransitionException(
                $"Plan step '{command.StepId}' cannot transition directly from Pending to '{command.Status}'.");
        }

        if (command.Status is not (PlanStepExecutionStatus.InProgress or PlanStepExecutionStatus.Completed))
            return;

        var definition = workflow.ApprovedSnapshot?.Steps.SingleOrDefault(step =>
            string.Equals(step.Id, command.StepId, StringComparison.Ordinal))
            ?? throw new PlanTransitionException(
                $"Approved plan definition for step '{command.StepId}' was not found.");
        var unresolved = definition.DependsOn
            .Where(dependencyId => workflow.StepExecutions.SingleOrDefault(step =>
                    string.Equals(step.StepId, dependencyId, StringComparison.Ordinal))?.Status
                != PlanStepExecutionStatus.Completed)
            .ToArray();
        if (unresolved.Length > 0)
        {
            throw new PlanTransitionException(
                $"Plan step '{command.StepId}' is blocked by incomplete dependencies: {string.Join(", ", unresolved)}.");
        }
    }

    private static PlanWorkflow Failure(
        PlanWorkflow current,
        string code,
        string message,
        DateTimeOffset occurredAt)
        => current with
        {
            State = PlanWorkflowState.Failed,
            LastErrorCode = code,
            LastErrorMessage = message,
            Version = current.Version + 1,
            UpdatedAt = occurredAt,
        };

    private static PlanTransitionException InvalidState(
        PlanWorkflow workflow,
        PlanWorkflowState expected)
        => new($"Plan workflow '{workflow.Id}' is in state '{workflow.State}', expected '{expected}'.");

    private static void ValidateExpectedVersion(PlanWorkflow? workflow, long expectedVersion)
    {
        var actualVersion = workflow?.Version ?? -1;
        if (actualVersion != expectedVersion)
            throw new PlanConcurrencyException(
                $"Plan workflow version conflict: expected {expectedVersion}, actual {actualVersion}.");
    }

    private static PlanRevisionResult DuplicateRevisionResult(PlanAggregate aggregate)
    {
        var revisionNumber = aggregate.Workflow.LastProcessedRevision
            ?? throw new PlanTransitionException("Duplicate revision command is missing its persisted revision reference.");
        var revision = aggregate.FindRevision(revisionNumber)
            ?? throw new PlanTransitionException($"Duplicate revision {revisionNumber} is missing from the Plan aggregate.");
        return new PlanRevisionResult(aggregate.Workflow, revision);
    }

    private static ApprovedPlanSnapshot FreezeApprovedSnapshot(PlanRevision revision, string approvedBy)
        => new()
        {
            PlanId = revision.PlanId,
            SessionId = revision.SessionId,
            Revision = revision.Revision,
            Markdown = revision.Markdown,
            Steps = revision.Steps,
            ContentHash = revision.ContentHash,
            ApprovedBy = approvedBy,
            ApprovedAt = DateTimeOffset.UtcNow,
        };

    private static Task<T> ExecuteAsync<T>(Func<Task<T>> action) => action();
}
