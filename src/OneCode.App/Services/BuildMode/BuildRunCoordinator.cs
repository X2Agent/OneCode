using OneCode.App.Services.Agent;
using OneCode.Core.Build;
using OneCode.Core.Tasks;
using TaskStatus = OneCode.Core.Tasks.TaskStatus;

namespace OneCode.App.Services.BuildMode;

public interface IBuildRunCoordinator
{
    Task<BuildRun> BeginOrResumeAsync(
        SessionId conversationId,
        string prompt,
        string workingDirectory,
        CancellationToken ct = default,
        Action<BuildRun>? durableStateObserver = null,
        BuildPlan? prescribedPlan = null);

    Task<BuildRun> PrepareAttemptAsync(
        BuildRunId runId,
        long expectedWorkflowFencingToken,
        CancellationToken ct = default);

    Task<BuildRun> ApprovePlanAsync(
        BuildRunId runId,
        ApprovedToolPolicy policy,
        string approvalSource,
        CancellationToken ct = default);

    Task<BuildRun> RejectPlanAsync(
        BuildRunId runId,
        string reason,
        CancellationToken ct = default);

    Task<BuildRun> BeginVerificationAsync(
        BuildRunId runId,
        CancellationToken ct = default,
        long? expectedWorkflowFencingToken = null);

    Task<BuildRun> CompleteAsync(
        BuildRunId runId,
        MainAgentRunResult result,
        CancellationToken ct = default,
        long? expectedWorkflowFencingToken = null);

    Task<BuildRun> ConfirmCommitAsync(
        BuildRunId runId,
        CancellationToken ct = default,
        long? expectedWorkflowFencingToken = null);
}

/// <summary>
/// Application control plane for Build mode. ChatService owns streaming only;
/// lifecycle, persistence, clarification and completion gates live here.
/// </summary>
public sealed partial class BuildRunCoordinator(
    IBuildRunStore store,
    IWorkspaceFingerprintProvider fingerprintProvider,
    RequirementAssessmentService assessmentService,
    BuildStateTransitionService transitions,
    ITaskService taskService,
    ILogger<BuildRunCoordinator> logger) : IBuildRunCoordinator
{
    public async Task<BuildRun> BeginOrResumeAsync(
        SessionId conversationId,
        string prompt,
        string workingDirectory,
        CancellationToken ct = default,
        Action<BuildRun>? durableStateObserver = null,
        BuildPlan? prescribedPlan = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (prescribedPlan is not null)
            BuildPlanValidator.Validate(prescribedPlan);

        var existing = await store.LoadAsync(conversationId, ct).ConfigureAwait(false);
        if (existing is not null && !BuildStateTransitionService.IsTerminal(existing.State))
        {
            if (prescribedPlan is not null
                && existing.Plan is not null
                && !PlansMatch(existing.Plan, prescribedPlan))
            {
                throw new InvalidOperationException(
                    $"BuildRun '{existing.Id}' cannot replace its persisted plan during resume.");
            }

            var currentFingerprint = await fingerprintProvider.ComputeAsync(workingDirectory, ct).ConfigureAwait(false);
            var expectedFingerprint = existing.State == BuildRunState.Accepting
                ? existing.CommitWorkspaceFingerprint
                : existing.WorkspaceFingerprint;
            if (!string.Equals(expectedFingerprint, currentFingerprint, StringComparison.Ordinal))
            {
                var blocked = transitions.Transition(existing, BuildRunState.Blocked, DateTimeOffset.UtcNow) with
                {
                    TerminalReason = BuildTerminalReason.Blocked,
                    FailureSummary = existing.State == BuildRunState.Accepting
                        ? "Workspace changed after final validation; commit recovery requires manual reconciliation."
                        : "Workspace changed after the BuildRun checkpoint; re-baselining is required before writes can resume.",
                };
                return await SaveAndReloadAsync(
                    blocked,
                    existing.Version,
                    ct,
                    durableStateObserver).ConfigureAwait(false);
            }

            if (existing.State == BuildRunState.Accepting)
            {
                var committed = await ConfirmCommitAsync(existing.Id, ct).ConfigureAwait(false);
                durableStateObserver?.Invoke(committed);
                return committed;
            }

            if (existing.State is BuildRunState.Intake or BuildRunState.Assessing)
            {
                return await ContinueAssessmentAsync(
                    existing,
                    ct,
                    durableStateObserver).ConfigureAwait(false);
            }

            if (existing.State == BuildRunState.Clarifying)
            {
                // A persisted prescribed plan is already an approved scope contract. Older
                // checkpoints may have entered Clarifying before this invariant was enforced;
                // resume them directly instead of asking the user to approve the same plan again.
                if (existing.Plan is not null)
                {
                    return await PrepareForExecutionAsync(
                        existing,
                        CreateScope(existing.IntakePrompt, "prescribed-plan", DateTimeOffset.UtcNow, existing.Plan),
                        DateTimeOffset.UtcNow,
                        ct,
                        durableStateObserver,
                        existing.Plan).ConfigureAwait(false);
                }

                return await ContinueClarificationAsync(
                    existing,
                    prompt,
                    ct,
                    durableStateObserver).ConfigureAwait(false);
            }

            if (existing.State is BuildRunState.ScopeConfirmed or BuildRunState.Planning)
            {
                return await PrepareForExecutionAsync(
                    existing,
                    existing.Scope
                        ?? throw new InvalidDataException($"BuildRun '{existing.Id}' lost its confirmed scope."),
                    DateTimeOffset.UtcNow,
                    ct,
                    durableStateObserver,
                    prescribedPlan).ConfigureAwait(false);
            }

            // Planned is a user approval gate: the plan stays parked here until the user approves
            // the plan + tool policy (ApprovePlanAsync) or rejects it (RejectPlanAsync).
            if (existing.State == BuildRunState.Planned)
            {
                durableStateObserver?.Invoke(existing);
                return existing;
            }

            if (existing.State is BuildRunState.Implementing or BuildRunState.Verifying or BuildRunState.Recovering)
            {
                durableStateObserver?.Invoke(existing);
                return existing;
            }

            durableStateObserver?.Invoke(existing);
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var fingerprint = await fingerprintProvider.ComputeAsync(workingDirectory, ct).ConfigureAwait(false);
        var run = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = conversationId,
            State = BuildRunState.Created,
            IntakePrompt = prompt.Trim(),
            Plan = prescribedPlan,
            WorkingDirectory = Path.GetFullPath(workingDirectory),
            WorkspaceFingerprint = fingerprint,
            CreatedAt = now,
            UpdatedAt = now,
        };

        run = transitions.Transition(run, BuildRunState.Intake, now);
        run = await SaveAndReloadAsync(run, 0, ct, durableStateObserver).ConfigureAwait(false);

        var assessment = assessmentService.Assess(prompt);
        run = transitions.Transition(run, BuildRunState.Assessing, now) with
        {
            Assessment = assessment,
        };
        run = await SaveAndReloadAsync(run, run.Version, ct, durableStateObserver).ConfigureAwait(false);

        // A caller-supplied plan has already passed validation and represents an approved,
        // bounded execution contract. Prompt heuristics may enrich its assessment record,
        // but must not send that contract back through clarification.
        if (prescribedPlan is null && assessment.RequiresClarification)
        {
            run = transitions.Transition(run, BuildRunState.Clarifying, now) with
            {
                ClarificationQuestions = assessmentService.BuildClarificationQuestions(assessment, prompt),
            };
            return await SaveAndReloadAsync(run, run.Version, ct, durableStateObserver).ConfigureAwait(false);
        }

        return await PrepareForExecutionAsync(
            run,
            CreateScope(prompt, prescribedPlan is null ? "runtime-derived" : "prescribed-plan", now, prescribedPlan),
            now,
            ct,
            durableStateObserver,
            prescribedPlan).ConfigureAwait(false);
    }

    public async Task<BuildRun> PrepareAttemptAsync(
        BuildRunId runId,
        long expectedWorkflowFencingToken,
        CancellationToken ct = default)
    {
        var current = await store.LoadByIdAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"BuildRun '{runId}' was not found.");
        ValidateWorkflowFencing(current, expectedWorkflowFencingToken);
        if (current.TransactionCommitted)
        {
            var blocked = transitions.Transition(current, BuildRunState.Blocked, DateTimeOffset.UtcNow) with
            {
                TerminalReason = BuildTerminalReason.Blocked,
                FailureSummary = "A non-terminal BuildRun is already marked committed; manual reconciliation is required.",
            };
            return await SaveAndReloadAsync(blocked, current.Version, ct).ConfigureAwait(false);
        }
        if (current.State == BuildRunState.Implementing)
            return current;
        if (current.State is not (BuildRunState.Verifying or BuildRunState.Recovering))
        {
            throw new InvalidOperationException(
                $"BuildRun '{runId}' cannot prepare an attempt from state '{current.State}'.");
        }

        var prepared = ResetLinkedTasksForRecovery(current) with
        {
            State = BuildRunState.Implementing,
            Validations = [],
            ChangedFiles = [],
            ToolBatches = [],
            DeliveryManifest = null,
            TerminalReason = null,
            FailureSummary = null,
            TransactionCommitted = false,
            TransactionRolledBack = false,
            SequenceNumber = current.SequenceNumber + 1,
            Version = current.Version + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        return await SaveAndReloadAsync(prepared, current.Version, ct).ConfigureAwait(false);
    }

    public async Task<BuildRun> ApprovePlanAsync(
        BuildRunId runId,
        ApprovedToolPolicy policy,
        string approvalSource,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.ToolNames is not { Count: > 0 })
            throw new ArgumentException("Approved tool policy must contain at least one tool.", nameof(policy));
        if (string.IsNullOrWhiteSpace(approvalSource))
            throw new ArgumentException("Approval source is required.", nameof(approvalSource));

        var current = await store.LoadByIdAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"BuildRun '{runId}' was not found.");
        if (current.State != BuildRunState.Planned)
        {
            throw new InvalidOperationException(
                $"BuildRun '{runId}' cannot approve a plan from state '{current.State}'.");
        }
        if (current.Plan is null)
            throw new InvalidOperationException($"BuildRun '{runId}' has no plan to approve.");
        if (current.ApprovedToolPolicy is not null)
        {
            // Idempotent approval: a persisted approval is already the approved contract.
            if (!string.Equals(current.PlanApprovalSource, approvalSource, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"BuildRun '{runId}' already has an approved tool policy from '{current.PlanApprovalSource}'.");
            }
            return current;
        }

        // Re-baseline before execution starts: the workspace must not drift between plan
        // generation and user approval (B-01: "恢复前和提交前校验工作区漂移").
        if (!string.IsNullOrWhiteSpace(current.WorkingDirectory)
            && !string.IsNullOrWhiteSpace(current.WorkspaceFingerprint))
        {
            var currentFingerprint = await fingerprintProvider.ComputeAsync(
                current.WorkingDirectory,
                ct).ConfigureAwait(false);
            if (!string.Equals(currentFingerprint, current.WorkspaceFingerprint, StringComparison.Ordinal))
            {
                var blocked = transitions.Transition(current, BuildRunState.Blocked, DateTimeOffset.UtcNow) with
                {
                    TerminalReason = BuildTerminalReason.Blocked,
                    FailureSummary = "Workspace changed after the plan was generated; re-baselining is required before approval.",
                    PlanRejectionReason = "workspace-drift",
                };
                return await SaveAndReloadAsync(blocked, current.Version, ct).ConfigureAwait(false);
            }
        }

        var now = DateTimeOffset.UtcNow;
        var approved = current with
        {
            ApprovedToolPolicy = policy,
            PlanApprovedAt = now,
            PlanApprovalSource = approvalSource,
            PlanRejectionReason = null,
        };
        var implementing = transitions.Transition(approved, BuildRunState.Implementing, now);
        return await SaveAndReloadAsync(implementing, current.Version, ct).ConfigureAwait(false);
    }

    public async Task<BuildRun> RejectPlanAsync(
        BuildRunId runId,
        string reason,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Rejection reason is required.", nameof(reason));

        var current = await store.LoadByIdAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"BuildRun '{runId}' was not found.");
        if (current.State != BuildRunState.Planned)
        {
            throw new InvalidOperationException(
                $"BuildRun '{runId}' cannot reject a plan from state '{current.State}'.");
        }

        var blocked = transitions.Transition(current, BuildRunState.Blocked, DateTimeOffset.UtcNow) with
        {
            TerminalReason = BuildTerminalReason.Blocked,
            FailureSummary = $"Plan was rejected by the user: {reason}",
            PlanRejectionReason = reason,
        };
        return await SaveAndReloadAsync(blocked, current.Version, ct).ConfigureAwait(false);
    }

    public async Task<BuildRun> BeginVerificationAsync(
        BuildRunId runId,
        CancellationToken ct = default,
        long? expectedWorkflowFencingToken = null)
    {
        var current = await store.LoadByIdAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"BuildRun '{runId}' was not found.");
        ValidateWorkflowFencing(current, expectedWorkflowFencingToken);

        if (current.State == BuildRunState.Verifying)
            return current;
        if (current.State != BuildRunState.Implementing)
        {
            throw new InvalidOperationException(
                $"BuildRun '{runId}' cannot begin verification from state {current.State}.");
        }

        var verifying = transitions.Transition(current, BuildRunState.Verifying, DateTimeOffset.UtcNow);
        return await SaveAndReloadAsync(verifying, current.Version, ct).ConfigureAwait(false);
    }

    public async Task<BuildRun> ConfirmCommitAsync(
        BuildRunId runId,
        CancellationToken ct = default,
        long? expectedWorkflowFencingToken = null)
    {
        var current = await store.LoadByIdAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"BuildRun '{runId}' was not found.");
        ValidateWorkflowFencing(current, expectedWorkflowFencingToken);

        if (BuildStateTransitionService.IsTerminal(current.State))
            return current;
        if (current.State != BuildRunState.Accepting)
            throw new InvalidOperationException(
                $"BuildRun '{runId}' cannot confirm commit from state {current.State}.");
        if (string.IsNullOrWhiteSpace(current.WorkingDirectory)
            || string.IsNullOrWhiteSpace(current.CommitWorkspaceFingerprint))
        {
            throw new InvalidOperationException(
                $"BuildRun '{runId}' has no durable commit fingerprint.");
        }

        var currentFingerprint = await fingerprintProvider.ComputeAsync(
            current.WorkingDirectory,
            ct).ConfigureAwait(false);
        if (!string.Equals(
                currentFingerprint,
                current.CommitWorkspaceFingerprint,
                StringComparison.Ordinal))
        {
            var blocked = transitions.Transition(current, BuildRunState.Blocked, DateTimeOffset.UtcNow) with
            {
                TerminalReason = BuildTerminalReason.Blocked,
                FailureSummary = "Workspace changed after final validation and before commit confirmation.",
            };
            return await SaveAndReloadAsync(blocked, current.Version, ct).ConfigureAwait(false);
        }

        var committed = current with
        {
            TransactionCommitted = true,
            TransactionRolledBack = false,
        };
        var completed = transitions.Transition(committed, BuildRunState.Completed, DateTimeOffset.UtcNow);
        return await SaveAndReloadAsync(completed, current.Version, ct).ConfigureAwait(false);
    }

    public async Task<BuildRun> CompleteAsync(
        BuildRunId runId,
        MainAgentRunResult result,
        CancellationToken ct = default,
        long? expectedWorkflowFencingToken = null)
    {
        var current = await store.LoadByIdAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"BuildRun '{runId}' was not found.");
        ValidateWorkflowFencing(current, expectedWorkflowFencingToken);

        if (BuildStateTransitionService.IsTerminal(current.State))
            return current;
        if (current.State is not (BuildRunState.Implementing or BuildRunState.Verifying))
            throw new InvalidOperationException($"BuildRun '{runId}' cannot complete from state {current.State}.");

        var now = DateTimeOffset.UtcNow;
        if (current.State == BuildRunState.Implementing)
        {
            current = transitions.Transition(current, BuildRunState.Verifying, now);
            current = await SaveAndReloadAsync(current, current.Version, ct).ConfigureAwait(false);
        }

        var validationStatus = result.FinalValidationStatus;
        var validation = new BuildValidationRun(
            $"validation-{current.SequenceNumber}",
            validationStatus,
            current.Plan?.ValidationCommands ?? [],
            string.IsNullOrWhiteSpace(result.ValidationFailureSummary)
                ? [validationStatus == BuildValidationStatus.Passed
                    ? "Final validation passed."
                    : "Final validation did not produce passing evidence."]
                : [result.ValidationFailureSummary],
            now,
            DateTimeOffset.UtcNow);

        current = current with
        {
            Validations = [.. current.Validations, validation],
            ChangedFiles = result.ModifiedFiles ?? [],
            ToolBatches = result.CompletedToolBatches ?? [],
            TransactionCommitted = result.TransactionCommitted,
            TransactionRolledBack = result.TransactionRolledBack,
            Metrics = current.Metrics with
            {
                TurnsCompleted = result.TurnCount,
                InputTokens = result.TotalInputTokens,
                OutputTokens = result.TotalOutputTokens,
            },
        };

        if (result.TerminalReason != BuildTerminalReason.Completed)
        {
            var target = result.TerminalReason switch
            {
                BuildTerminalReason.Cancelled => BuildRunState.Cancelled,
                BuildTerminalReason.TurnLimitReached => BuildRunState.LimitReached,
                BuildTerminalReason.BudgetExceeded => BuildRunState.BudgetExceeded,
                BuildTerminalReason.PermissionRefused => BuildRunState.Blocked,
                _ => BuildRunState.Failed,
            };
            current = transitions.Transition(current, target, DateTimeOffset.UtcNow) with
            {
                TerminalReason = result.TerminalReason,
                FailureSummary = result.ValidationFailureSummary ?? result.BudgetExceededReason,
            };
            MarkLinkedTasksTerminal(
                current,
                target == BuildRunState.Cancelled ? TaskStatus.Cancelled : TaskStatus.Failed);
            return await SaveAndReloadAsync(current, current.Version, ct).ConfigureAwait(false);
        }

        if (validationStatus != BuildValidationStatus.Passed)
        {
            current = transitions.Transition(current, BuildRunState.Failed, DateTimeOffset.UtcNow) with
            {
                TerminalReason = BuildTerminalReason.ValidationFailed,
                FailureSummary = result.ValidationFailureSummary ?? "Final validation did not pass.",
            };
            MarkLinkedTasksTerminal(current, TaskStatus.Failed);
            return await SaveAndReloadAsync(current, current.Version, ct).ConfigureAwait(false);
        }

        current = transitions.Transition(current, BuildRunState.Accepting, DateTimeOffset.UtcNow);
        try
        {
            ValidateChangedFileAttribution(current);
            current = MarkExecutionEvidencePassed(current);
        }
        catch (InvalidOperationException ex)
        {
            current = transitions.Transition(current, BuildRunState.Failed, DateTimeOffset.UtcNow) with
            {
                TerminalReason = BuildTerminalReason.ValidationFailed,
                FailureSummary = ex.Message,
            };
            MarkLinkedTasksTerminal(current, TaskStatus.Failed);
            return await SaveAndReloadAsync(current, current.Version, ct).ConfigureAwait(false);
        }
        var commitFingerprint = await fingerprintProvider.ComputeAsync(
            current.WorkingDirectory
                ?? throw new InvalidOperationException($"BuildRun '{runId}' has no working directory."),
            ct).ConfigureAwait(false);
        current = current with
        {
            TransactionCommitted = current.ChangedFiles.Count == 0,
            CommitWorkspaceFingerprint = commitFingerprint,
            DeliveryManifest = CreateDeliveryManifest(current),
            TerminalReason = BuildTerminalReason.Completed,
        };
        return await SaveAndReloadAsync(current, current.Version, ct).ConfigureAwait(false);
    }

    private async Task<BuildRun> ContinueAssessmentAsync(
        BuildRun current,
        CancellationToken ct,
        Action<BuildRun>? durableStateObserver = null)
    {
        var now = DateTimeOffset.UtcNow;
        var run = current;
        if (run.State == BuildRunState.Intake)
        {
            run = transitions.Transition(run, BuildRunState.Assessing, now);
            run = await SaveAndReloadAsync(
                run,
                run.Version,
                ct,
                durableStateObserver).ConfigureAwait(false);
        }

        var assessment = run.Assessment ?? assessmentService.Assess(run.IntakePrompt);
        if (run.Plan is null && assessment.RequiresClarification)
        {
            run = transitions.Transition(run, BuildRunState.Clarifying, now) with
            {
                Assessment = assessment,
                ClarificationQuestions = assessmentService.BuildClarificationQuestions(assessment, run.IntakePrompt),
            };
            return await SaveAndReloadAsync(
                run,
                run.Version,
                ct,
                durableStateObserver).ConfigureAwait(false);
        }

        run = run with { Assessment = assessment };
        return await PrepareForExecutionAsync(
            run,
            CreateScope(run.IntakePrompt, run.Plan is null ? "runtime-derived" : "prescribed-plan", now, run.Plan),
            now,
            ct,
            durableStateObserver,
            run.Plan).ConfigureAwait(false);
    }

    private async Task<BuildRun> ContinueClarificationAsync(
        BuildRun current,
        string response,
        CancellationToken ct,
        Action<BuildRun>? durableStateObserver = null)
    {
        var now = DateTimeOffset.UtcNow;
        if (current.ProposedScope is not null && IsConfirmation(response))
        {
            var confirmed = current.ProposedScope with
            {
                ConfirmedBy = "user",
                ConfirmedAt = now,
            };
            return await PrepareForExecutionAsync(
                current,
                confirmed,
                now,
                ct,
                durableStateObserver).ConfigureAwait(false);
        }

        var combined = $"{current.IntakePrompt}\nClarification response: {response.Trim()}";
        var assessment = assessmentService.Assess(combined);
        var questions = assessmentService.BuildClarificationQuestions(assessment, current.IntakePrompt);
        BuildScopeSnapshot? proposed = null;
        if (!assessment.RequiresClarification)
        {
            proposed = CreateScope(combined, "pending-user-confirmation", now, current.Plan);
            questions = ["开始修改前，请确认建议的任务范围；也可以取消或补充修正。"];
        }

        var updated = current with
        {
            IntakePrompt = combined,
            Assessment = assessment,
            ProposedScope = proposed,
            ClarificationQuestions = questions,
            SequenceNumber = current.SequenceNumber + 1,
            UpdatedAt = now,
        };
        return await SaveAndReloadAsync(updated, current.Version, ct).ConfigureAwait(false);
    }

    private async Task<BuildRun> PrepareForExecutionAsync(
        BuildRun current,
        BuildScopeSnapshot scope,
        DateTimeOffset now,
        CancellationToken ct,
        Action<BuildRun>? durableStateObserver = null,
        BuildPlan? prescribedPlan = null)
    {
        var run = current;
        if (run.State is BuildRunState.Assessing or BuildRunState.Clarifying)
        {
            run = transitions.Transition(run, BuildRunState.ScopeConfirmed, now) with
            {
                ProposedScope = null,
                Scope = scope,
                ClarificationQuestions = [],
            };
            run = await SaveAndReloadAsync(
                run,
                current.Version,
                ct,
                durableStateObserver).ConfigureAwait(false);
        }

        if (run.State == BuildRunState.ScopeConfirmed)
        {
            run = transitions.Transition(run, BuildRunState.Planning, now);
            run = await SaveAndReloadAsync(
                run,
                run.Version,
                ct,
                durableStateObserver).ConfigureAwait(false);
        }

        if (run.State == BuildRunState.Planning)
        {
            var plan = run.Plan
                ?? prescribedPlan
                ?? CreateQuickFixPlan(scope);
            BuildPlanValidator.Validate(plan);
            plan = LinkPlanTasks(run, plan);
            run = run with { Plan = plan };
            run = transitions.Transition(run, BuildRunState.Planned, now);
            run = await SaveAndReloadAsync(
                run,
                run.Version,
                ct,
                durableStateObserver).ConfigureAwait(false);
        }

        // Planned is a terminal park for this method: execution starts only after the user
        // approves the plan + tool policy via ApprovePlanAsync (see IBuildRunCoordinator).
        return run;
    }

    private async Task<BuildRun> SaveAndReloadAsync(
        BuildRun run,
        long expectedVersion,
        CancellationToken ct,
        Action<BuildRun>? durableStateObserver = null)
    {
        if (run.WorkflowFencingToken is { } fencingToken)
            await store.SaveFencedAsync(run, expectedVersion, fencingToken, ct).ConfigureAwait(false);
        else
            await store.SaveAsync(run, expectedVersion, ct).ConfigureAwait(false);
        var saved = await store.LoadByIdAsync(run.Id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"BuildRun '{run.Id}' could not be reloaded after save.");
        logger.LogDebug("BuildRun {RunId} persisted in state {State} at version {Version}", saved.Id, saved.State, saved.Version);
        durableStateObserver?.Invoke(saved);
        return saved;
    }

    private static void ValidateWorkflowFencing(
        BuildRun run,
        long? expectedWorkflowFencingToken)
    {
        if (run.WorkflowFencingToken is not { } currentToken)
        {
            if (expectedWorkflowFencingToken is not null)
                throw new InvalidOperationException("BuildRun has not been workflow-claimed.");
            return;
        }
        if (expectedWorkflowFencingToken != currentToken)
            throw new InvalidOperationException("Stale BuildRun workflow fencing token.");
    }

    private static bool IsConfirmation(string response) =>
        response.Trim().Equals("confirm", StringComparison.OrdinalIgnoreCase)
        || response.Trim().Equals("confirmed", StringComparison.OrdinalIgnoreCase)
        || response.Trim().Equals("确认", StringComparison.Ordinal)
        || response.Trim().Equals("确认执行", StringComparison.Ordinal);
}