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
public sealed class BuildRunCoordinator(
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

    private BuildPlan LinkPlanTasks(BuildRun run, BuildPlan plan)
    {
        var linked = new Dictionary<string, TaskItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var planTask in TopologicalOrder(plan.Tasks))
        {
            var dependencies = planTask.DependsOn
                .Select(id => linked[id].Id)
                .ToArray();
            linked[planTask.Id] = FindOrCreatePlanTask(run, planTask, dependencies);
        }

        return plan with
        {
            Tasks = plan.Tasks.Select(planTask => planTask with
            {
                Status = planTask.DependsOn.Count == 0
                    ? BuildTaskStatus.InProgress
                    : BuildTaskStatus.Pending,
                Evidence = planTask.Evidence ?? [],
                TaskItemId = linked[planTask.Id].Id,
            }).ToArray(),
        };
    }

    private TaskItem FindOrCreatePlanTask(
        BuildRun run,
        BuildPlanTask planTask,
        IReadOnlyList<string> blockedBy)
    {
        var existing = taskService.ListTasks(
                conversationId: run.ConversationId?.ToString(),
                buildRunId: run.Id.ToString(),
                exactScope: true)
            .SingleOrDefault(task =>
                task.Metadata?.ExtraProperties?.TryGetValue("BuildPlanTaskId", out var mappedId) == true
                && string.Equals(mappedId, planTask.Id, StringComparison.Ordinal));
        if (existing is not null)
            return existing;

        return taskService.CreateTask(
            planTask.Title,
            planTask.Description,
            $"Executing {planTask.Title}",
            status: blockedBy.Count == 0 ? TaskStatus.InProgress : TaskStatus.Pending,
            blockedBy: blockedBy,
            metadata: new TaskMetadata(
                ExtraProperties: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["BuildPlanTaskId"] = planTask.Id,
                }),
            conversationId: run.ConversationId?.ToString(),
            buildRunId: run.Id.ToString());
    }

    private static BuildPlan CreateQuickFixPlan(BuildScopeSnapshot scope)
        => new(
            "Execute the confirmed Build scope and produce deterministic validation evidence.",
            [new BuildPlanTask(
                "implementation",
                "Implement confirmed scope",
                scope.Goal,
                [],
                [],
                scope.AcceptanceCriteria.Select(item => item.Id).ToArray())],
            [],
            [],
            scope.OutOfScope);

    private static IReadOnlyList<BuildPlanTask> TopologicalOrder(
        IReadOnlyList<BuildPlanTask> tasks)
    {
        var byId = tasks.ToDictionary(task => task.Id, StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<BuildPlanTask>(tasks.Count);

        void Visit(BuildPlanTask task)
        {
            if (!visited.Add(task.Id))
                return;
            foreach (var dependency in task.DependsOn)
                Visit(byId[dependency]);
            ordered.Add(task);
        }

        foreach (var task in tasks)
            Visit(task);
        return ordered;
    }

    private static bool PlansMatch(BuildPlan persisted, BuildPlan prescribed)
        => string.Equals(persisted.Summary, prescribed.Summary, StringComparison.Ordinal)
            && persisted.RequireExplicitTaskCompletion == prescribed.RequireExplicitTaskCompletion
            && persisted.Tasks.Select(TaskIdentity)
                .SequenceEqual(prescribed.Tasks.Select(TaskIdentity));

    private static string TaskIdentity(BuildPlanTask task)
        => string.Join(
            "\u001f",
            task.Id,
            task.Title,
            task.Description,
            string.Join("\u001e", task.DependsOn),
            string.Join("\u001e", task.ExpectedFiles),
            string.Join("\u001e", task.AcceptanceCriteria));

    private static BuildScopeSnapshot CreateScope(
        string prompt,
        string confirmedBy,
        DateTimeOffset now,
        BuildPlan? prescribedPlan = null)
    {
        var taskAcceptance = prescribedPlan?.Tasks
            .SelectMany(task => task.AcceptanceCriteria.Select((criterion, index) =>
                new AcceptanceCriterion(
                    $"task:{task.Id}:{index}",
                    criterion,
                    Required: true)))
            .ToArray() ?? [];
        IReadOnlyList<AcceptanceCriterion> acceptance = taskAcceptance.Length > 0
            ? [.. taskAcceptance, new AcceptanceCriterion(
                "final-validation",
                "Final deterministic validation passes after the last file change.",
                Required: true)]
            : [new AcceptanceCriterion(
                "final-validation",
                "Final deterministic validation passes after the last file change.",
                Required: true)];
        return new BuildScopeSnapshot(
            prompt.Trim(),
            [prompt.Trim()],
            [],
            ["Preserve repository conventions and pass final validation."],
            acceptance,
            confirmedBy,
            now);
    }

    private static void ValidateChangedFileAttribution(BuildRun run)
    {
        var tasks = run.Plan?.Tasks
            ?? throw new InvalidOperationException("BuildRun has no plan for file attribution.");
        if (tasks.Count <= 1)
            return;

        foreach (var changedFile in run.ChangedFiles)
        {
            var matches = tasks.Count(task => task.ExpectedFiles.Any(expected =>
                PathsMatch(expected, changedFile)));
            if (matches != 1)
            {
                throw new InvalidOperationException(
                    matches == 0
                        ? $"Changed file '{changedFile}' is not attributed to any BuildPlan task."
                        : $"Changed file '{changedFile}' is ambiguously attributed to {matches} BuildPlan tasks.");
            }
        }
    }

    private BuildRun MarkExecutionEvidencePassed(BuildRun run)
    {
        var plan = run.Plan ?? throw new InvalidOperationException("BuildRun has no plan to complete.");
        var validationEvidence = run.Validations[^1].Evidence.Select((evidence, index) =>
            new BuildTaskEvidence(
                BuildEvidenceKind.Validation,
                $"{run.Validations[^1].Id}:{index}",
                evidence)).ToArray();
        var singleTaskPlan = plan.Tasks.Count == 1;

        var completedTasks = plan.Tasks.Select(planTask =>
        {
            var taskItem = GetLinkedTask(run, planTask);
            if (plan.RequireExplicitTaskCompletion && taskItem.Status != TaskStatus.Completed)
            {
                throw new InvalidOperationException(
                    $"BuildPlan task '{planTask.Id}' has not been explicitly completed by its execution protocol.");
            }
            if (!plan.RequireExplicitTaskCompletion && taskItem.Status != TaskStatus.Completed)
            {
                if (!taskService.UpdateTask(taskItem.Id, status: TaskStatus.Completed))
                {
                    throw new InvalidOperationException(
                        $"Linked task '{taskItem.Id}' for BuildPlan task '{planTask.Id}' could not be completed.");
                }

                taskItem = GetLinkedTask(run, planTask);
            }

            var fileEvidence = run.ChangedFiles
                .Where(file => singleTaskPlan || planTask.ExpectedFiles.Any(expected =>
                    PathsMatch(expected, file)))
                .Select(file => new BuildTaskEvidence(
                    BuildEvidenceKind.FileChange,
                    file,
                    $"File changed for BuildPlan task {planTask.Id}."));
            var toolEvidence = run.ToolBatches.SelectMany(batch => batch.Calls
                .Where(call => singleTaskPlan || ToolCallBelongsToTask(call, planTask))
                .Select(call => new BuildTaskEvidence(
                    BuildEvidenceKind.ToolCall,
                    call.CallId,
                    $"{call.ToolName} completed in batch {batch.BatchId}.")));
            var persistedEvidence = ReadProjectedEvidence(taskItem)
                .Select((item, index) => new BuildTaskEvidence(
                    BuildEvidenceKind.Acceptance,
                    $"{taskItem.Id}:projection:{index}",
                    item));
            var evidence = new List<BuildTaskEvidence>
            {
                new(
                    BuildEvidenceKind.TaskCompletion,
                    taskItem.Id,
                    $"Persistent task '{taskItem.Id}' completed for BuildPlan task '{planTask.Id}'."),
            };
            evidence.AddRange(fileEvidence);
            evidence.AddRange(toolEvidence);
            evidence.AddRange(persistedEvidence);
            evidence.AddRange(validationEvidence);

            return planTask with
            {
                Status = BuildTaskStatus.Completed,
                Evidence = evidence,
            };
        }).ToArray();

        var evidenceByTask = completedTasks.ToDictionary(
            task => task.Id,
            task => task.CompletionEvidence,
            StringComparer.Ordinal);
        var scope = run.Scope! with
        {
            AcceptanceCriteria = run.Scope!.AcceptanceCriteria.Select(item =>
            {
                if (TryParseTaskAcceptanceId(item.Id, out var taskId)
                    && evidenceByTask.TryGetValue(taskId, out var taskEvidence))
                {
                    var mapped = taskEvidence.FirstOrDefault(evidence =>
                            evidence.Kind == BuildEvidenceKind.Acceptance)
                        ?? taskEvidence.FirstOrDefault(evidence =>
                            evidence.Kind == BuildEvidenceKind.FileChange)
                        ?? taskEvidence.FirstOrDefault(evidence =>
                            evidence.Kind == BuildEvidenceKind.ToolCall);
                    if (mapped is null)
                    {
                        throw new InvalidOperationException(
                            $"Acceptance criterion '{item.Id}' has no task-specific evidence for BuildPlan task '{taskId}'.");
                    }

                    return item with
                    {
                        Status = AcceptanceStatus.Passed,
                        Evidence = mapped.Summary,
                    };
                }

                return item with
                {
                    Status = AcceptanceStatus.Passed,
                    Evidence = run.Validations[^1].Evidence.FirstOrDefault() ?? "Final validation passed.",
                };
            }).ToArray(),
        };
        return run with { Plan = plan with { Tasks = completedTasks }, Scope = scope };
    }

    private static IReadOnlyList<string> ReadProjectedEvidence(TaskItem task)
        => (task.OutputLog ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("[projection:", StringComparison.Ordinal))
            .Select(line =>
            {
                var markerEnd = line.IndexOf("] ", StringComparison.Ordinal);
                return markerEnd >= 0 ? line[(markerEnd + 2)..] : line;
            })
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static bool TryParseTaskAcceptanceId(string acceptanceId, out string taskId)
    {
        taskId = string.Empty;
        if (!acceptanceId.StartsWith("task:", StringComparison.Ordinal))
            return false;
        var separator = acceptanceId.LastIndexOf(':');
        if (separator <= "task:".Length)
            return false;
        taskId = acceptanceId["task:".Length..separator];
        return !string.IsNullOrWhiteSpace(taskId);
    }

    private static bool ToolCallBelongsToTask(
        OneCode.Core.Tools.CompletedToolCallRecord call,
        BuildPlanTask task)
    {
        if (string.Equals(call.ToolName, "UpdatePlanStep", StringComparison.OrdinalIgnoreCase)
            && TryReadStringArgument(call.ArgumentsJson, "stepId", out var stepId))
        {
            return string.Equals(stepId, task.Id, StringComparison.Ordinal);
        }

        return task.ExpectedFiles.Any(expected =>
            call.ArgumentsJson.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReadStringArgument(
        string argumentsJson,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String
                && property.GetString() is { } parsed)
            {
                value = parsed;
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool PathsMatch(string expected, string changed)
        => string.Equals(
            expected.Replace('\\', '/').TrimStart('.', '/'),
            changed.Replace('\\', '/').TrimStart('.', '/'),
            StringComparison.OrdinalIgnoreCase);

    private TaskItem GetLinkedTask(BuildRun run, BuildPlanTask planTask)
    {
        if (string.IsNullOrWhiteSpace(planTask.TaskItemId))
        {
            throw new InvalidOperationException(
                $"BuildPlan task '{planTask.Id}' has no persistent TaskItem mapping.");
        }

        var taskItem = taskService.GetTask(planTask.TaskItemId)
            ?? throw new InvalidOperationException(
                $"Persistent task '{planTask.TaskItemId}' for BuildPlan task '{planTask.Id}' was not found.");
        var expectedConversationId = run.ConversationId?.ToString();
        if (!string.Equals(taskItem.ConversationId, expectedConversationId, StringComparison.Ordinal)
            || !string.Equals(taskItem.BuildRunId, run.Id.ToString(), StringComparison.Ordinal)
            || taskItem.Metadata?.ExtraProperties?.TryGetValue("BuildPlanTaskId", out var mappedTaskId) != true
            || !string.Equals(mappedTaskId, planTask.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Persistent task '{taskItem.Id}' does not belong to BuildPlan task '{planTask.Id}' in BuildRun '{run.Id}'.");
        }

        return taskItem;
    }

    private BuildRun ResetLinkedTasksForRecovery(BuildRun run)
    {
        if (run.Plan is null)
            return run;

        var tasks = run.Plan.Tasks.Select(planTask =>
        {
            if (string.IsNullOrWhiteSpace(planTask.TaskItemId))
                return planTask;

            var taskItem = GetLinkedTask(run, planTask);
            if (taskItem.Status == TaskStatus.Completed)
            {
                return planTask with
                {
                    Status = BuildTaskStatus.Completed,
                    Evidence = planTask.Evidence ?? [],
                };
            }

            var targetStatus = taskItem.BlockedBy.All(dependencyId =>
                taskService.GetTask(dependencyId)?.Status == TaskStatus.Completed)
                    ? TaskStatus.InProgress
                    : TaskStatus.Pending;
            var projected = taskService.ProjectTaskStatus(taskItem.Id, targetStatus);
            if (!projected.Succeeded)
            {
                throw new InvalidOperationException(
                    projected.Error
                    ?? $"Persistent task '{taskItem.Id}' could not be reset for BuildRun recovery.");
            }

            return planTask with
            {
                Status = targetStatus == TaskStatus.InProgress
                    ? BuildTaskStatus.InProgress
                    : BuildTaskStatus.Pending,
                Evidence = [],
            };
        }).ToArray();

        return run with { Plan = run.Plan with { Tasks = tasks } };
    }

    private void MarkLinkedTasksTerminal(BuildRun run, TaskStatus status)
    {
        foreach (var planTask in run.Plan?.Tasks ?? [])
        {
            if (string.IsNullOrWhiteSpace(planTask.TaskItemId))
                continue;

            var taskItem = taskService.GetTask(planTask.TaskItemId);
            if (taskItem is null
                || taskItem.Status is TaskStatus.Completed or TaskStatus.Failed or TaskStatus.Cancelled)
            {
                continue;
            }

            _ = taskService.UpdateTask(taskItem.Id, status: status);
        }
    }

    private static BuildDeliveryManifest CreateDeliveryManifest(BuildRun run) =>
        new(
            run.ChangedFiles,
            run.Plan!.Tasks
                .Where(task => task.Status == BuildTaskStatus.Completed)
                .Select(task => task.Id)
                .ToArray(),
            run.Validations.SelectMany(validation => validation.Evidence).ToArray(),
            run.Scope!.AcceptanceCriteria
                .Where(item => !string.IsNullOrWhiteSpace(item.Evidence))
                .Select(item => item.Evidence!)
                .ToArray(),
            run.Plan.Risks,
            DateTimeOffset.UtcNow);

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
