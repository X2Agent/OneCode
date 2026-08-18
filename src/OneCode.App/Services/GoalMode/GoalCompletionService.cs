using OneCode.App.Services.Agent;
using OneCode.App.Services.Lsp;
using OneCode.Core.Build;
using OneCode.Core.Goals;
using OneCode.Core.IO;
using OneCode.Core.Lsp;

namespace OneCode.App.Services.GoalMode;

internal interface IGoalCompletionService
{
    Task<GoalRun> CompleteAsync(GoalRun run, long fencingToken, CancellationToken ct);
}

internal sealed class GoalCompletionService(
    IGoalRunStore store,
    IGoalWorkspaceService workspaceService,
    IGoalStepExecutionService stepExecutionService,
    IVerificationProvider? verificationProvider = null,
    LspDiagnosticRegistry? diagnosticRegistry = null) : IGoalCompletionService
{
    public async Task<GoalRun> CompleteAsync(GoalRun run, long fencingToken, CancellationToken ct)
    {
        ValidateFencing(run, fencingToken);
        var current = await ReloadAsync(run.Id, ct).ConfigureAwait(false);
        ValidateFencing(current, fencingToken);
        if (current.IsTerminal)
            return current;
        if (current.State is not (GoalRunState.Executing or GoalRunState.Validating or GoalRunState.Publishing))
            throw new InvalidOperationException($"GoalRun '{current.Id}' cannot complete from state '{current.State}'.");

        if (current.State == GoalRunState.Publishing)
            return await PublishAsync(current, fencingToken, ct).ConfigureAwait(false);

        var report = new List<GoalGateEvidence>();
        var incompleteRequired = current.Plan
            .Where(step => !step.Optional && !IsSatisfied(step))
            .ToArray();
        report.Add(new GoalGateEvidence(
            "state-integrity",
            incompleteRequired.Length == 0,
            false,
            incompleteRequired.Length == 0
                ? "All required sub-goals are Completed."
                : $"Incomplete required sub-goals: {string.Join(", ", incompleteRequired.Select(step => $"#{step.Id}:{step.State}"))}."));
        if (incompleteRequired.Length > 0)
            return await FailAsync(current, fencingToken, report, "One or more required Goal steps are incomplete.", ct).ConfigureAwait(false);

        var executionByGoalId = current.Executions
            .GroupBy(execution => execution.GoalId)
            .ToDictionary(group => group.Key, group => group.Last());
        var evidenceComplete = current.Plan
            .Where(step => !step.Optional && !IsReplacedByDecomposition(step))
            .All(step => executionByGoalId.TryGetValue(step.Id, out var execution)
                && execution.State == GoalStepState.Completed
                && execution.Validations.All(gate => gate.Passed || gate.Skipped));
        report.Add(new GoalGateEvidence(
            "requirement-and-integration-coverage",
            evidenceComplete,
            false,
            evidenceComplete
                ? "Every required sub-goal has accepted deterministic evidence."
                : "One or more required sub-goals lack accepted deterministic evidence."));
        if (!evidenceComplete)
            return await FailAsync(current, fencingToken, report, "Required Goal evidence is incomplete.", ct).ConfigureAwait(false);

        var workspace = current.Workspace
            ?? throw new InvalidOperationException("GoalRun has no isolated workspace snapshot.");
        var changedFiles = current.Executions
            .SelectMany(execution => execution.ChangedFiles)
            .Select(path => Path.GetFullPath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var scopeErrors = changedFiles
            .Where(path => !PathBoundary.IsWithinDirectory(path, workspace.IsolatedPath))
            .ToArray();
        report.Add(new GoalGateEvidence(
            "change-scope",
            scopeErrors.Length == 0,
            false,
            scopeErrors.Length == 0
                ? $"All {changedFiles.Length} changed file(s) are inside the isolated Goal worktree."
                : $"Out-of-scope changes: {string.Join(", ", scopeErrors)}"));
        if (scopeErrors.Length > 0)
            return await FailAsync(current, fencingToken, report, "Goal changes escaped the isolated worktree.", ct).ConfigureAwait(false);

        var sourceFilesChanged = verificationProvider is not null
            && changedFiles.Any(verificationProvider.IsSourceFile);
        var testsRequired = current.Plan.Any(step => !step.Optional && step.RequiresTests);
        var buildRequired = sourceFilesChanged
            || current.Plan.Any(step => !step.Optional && step.RequiresBuild);
        if (buildRequired || testsRequired)
        {
            if (verificationProvider is null)
            {
                report.Add(new GoalGateEvidence(
                    testsRequired ? "build-and-test" : "build",
                    false,
                    false,
                    "No verification provider is registered."));
                return await FailAsync(current, fencingToken, report, "Goal verification is unavailable.", ct).ConfigureAwait(false);
            }
            var verification = testsRequired
                ? await verificationProvider.VerifyBuildAndTestsAsync(workspace.IsolatedPath, changedFiles, ct).ConfigureAwait(false)
                : await verificationProvider.VerifyAsync(workspace.IsolatedPath, changedFiles, ct).ConfigureAwait(false);
            var verificationPassed = verification.Success && !verification.Skipped;
            report.Add(new GoalGateEvidence(
                testsRequired ? "build-and-test" : "build",
                verificationPassed,
                false,
                verification.FormatForLlm()));
            if (!verificationPassed)
                return await FailAsync(current, fencingToken, report, "Goal build or test verification failed.", ct).ConfigureAwait(false);
        }
        else
        {
            report.Add(new GoalGateEvidence(
                "build-and-test",
                true,
                true,
                "No source changes or explicit build/test gates were required."));
        }

        var diagnostics = GetDiagnostics(workspace.IsolatedPath, changedFiles);
        report.Add(new GoalGateEvidence(
            "static-diagnostics",
            diagnostics.Count == 0,
            diagnosticRegistry is null,
            diagnosticRegistry is null
                ? "LSP diagnostics unavailable; build/test evidence remains authoritative."
                : diagnostics.Count == 0
                    ? "No unresolved LSP errors were reported for changed files."
                    : string.Join("; ", diagnostics)));
        if (diagnostics.Count > 0)
            return await FailAsync(current, fencingToken, report, "Goal static diagnostics failed.", ct).ConfigureAwait(false);

        var semantic = await stepExecutionService.EvaluateFinalGoalAsync(
            current.Goal,
            current.Plan.Select(ToGoalItem).ToArray(),
            current.Executions.Select(ToSubGoalExecution).ToArray(),
            ct).ConfigureAwait(false);
        report.Add(new GoalGateEvidence("final-semantic-review", semantic.Passed, false, semantic.Summary));
        var budget = current.Budget with
        {
            TotalInputTokens = current.Budget.TotalInputTokens + semantic.InputTokens,
            TotalOutputTokens = current.Budget.TotalOutputTokens + semantic.OutputTokens,
        };
        if (!semantic.Passed)
        {
            return await FailAsync(
                current with { Budget = budget },
                fencingToken,
                report,
                "Final Goal semantic review failed.",
                ct).ConfigureAwait(false);
        }

        var publishing = current with
        {
            State = GoalRunState.Publishing,
            Budget = budget,
            FinalValidation = report,
            FailureSummary = null,
            TerminalReason = null,
        };
        await SaveAsync(publishing, fencingToken, ct).ConfigureAwait(false);
        return await PublishAsync(await ReloadAsync(current.Id, ct).ConfigureAwait(false), fencingToken, ct)
            .ConfigureAwait(false);
    }

    private static bool IsSatisfied(GoalStepSnapshot step)
        => step.State == GoalStepState.Completed || IsReplacedByDecomposition(step);

    private static bool IsReplacedByDecomposition(GoalStepSnapshot step)
        => step.State == GoalStepState.Skipped && step.NeedsFurtherDecomposition;

    private async Task<GoalRun> PublishAsync(GoalRun current, long fencingToken, CancellationToken ct)
    {
        var receipt = await workspaceService.PublishAsync(current, fencingToken, ct).ConfigureAwait(false);
        var completed = current with
        {
            State = GoalRunState.Completed,
            PublishReceipt = receipt,
            TerminalReason = BuildTerminalReason.Completed,
            FailureSummary = null,
        };
        await SaveAsync(completed, fencingToken, ct).ConfigureAwait(false);
        return await ReloadAsync(current.Id, ct).ConfigureAwait(false);
    }

    private async Task<GoalRun> FailAsync(
        GoalRun current,
        long fencingToken,
        IReadOnlyList<GoalGateEvidence> report,
        string failureSummary,
        CancellationToken ct)
    {
        var failed = current with
        {
            State = current.State == GoalRunState.Paused ? GoalRunState.Paused : GoalRunState.Failed,
            FinalValidation = report,
            TerminalReason = current.State == GoalRunState.Paused
                ? BuildTerminalReason.BudgetExceeded
                : BuildTerminalReason.ValidationFailed,
            FailureSummary = failureSummary,
        };
        await SaveAsync(failed, fencingToken, ct).ConfigureAwait(false);
        return await ReloadAsync(current.Id, ct).ConfigureAwait(false);
    }

    private Task SaveAsync(GoalRun run, long fencingToken, CancellationToken ct)
        => store.SaveFencedAsync(run, run.Version, fencingToken, ct);

    private async Task<GoalRun> ReloadAsync(GoalRunId runId, CancellationToken ct)
        => await store.LoadByIdAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"GoalRun '{runId}' was not found.");

    private IReadOnlyList<string> GetDiagnostics(
        string workingDirectory,
        IReadOnlyList<string> changedFiles)
    {
        if (diagnosticRegistry is null || changedFiles.Count == 0)
            return [];
        var changed = changedFiles.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return diagnosticRegistry.GetAllDiagnostics()
            .Where(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error)
            .Where(diagnostic => changed.Contains(Path.GetFullPath(diagnostic.FilePath)))
            .Where(diagnostic => PathBoundary.IsWithinDirectory(diagnostic.FilePath, workingDirectory))
            .Select(diagnostic => diagnostic.Summary)
            .ToArray();
    }

    private static GoalItem ToGoalItem(GoalStepSnapshot item) => new()
    {
        Id = item.Id,
        Description = item.Description,
        SuccessCriteria = item.SuccessCriteria,
        Status = item.State switch
        {
            GoalStepState.Pending => GoalStatus.Pending,
            GoalStepState.InProgress => GoalStatus.InProgress,
            GoalStepState.Completed => GoalStatus.Completed,
            GoalStepState.Failed => GoalStatus.Failed,
            GoalStepState.Skipped => GoalStatus.Skipped,
            _ => throw new ArgumentOutOfRangeException(nameof(item)),
        },
        RequiredTools = item.RequiredTools,
        Depth = item.Depth,
        NeedsFurtherDecomposition = item.NeedsFurtherDecomposition,
        ExpectedFiles = item.ExpectedFiles,
        AllowedPaths = item.AllowedPaths,
        RequiresBuild = item.RequiresBuild,
        RequiresTests = item.RequiresTests,
        Optional = item.Optional,
    };

    private static SubGoalExecution ToSubGoalExecution(GoalStepExecutionEvidence evidence)
        => new(
            evidence.GoalId,
            evidence.State == GoalStepState.Completed ? GoalStatus.Completed : GoalStatus.Failed,
            evidence.Attempts,
            evidence.InputTokens,
            evidence.OutputTokens,
            evidence.AgentOutput,
            evidence.Evaluation,
            new SubGoalEvidence(
                evidence.AgentOutput,
                evidence.ChangedFiles,
                evidence.ToolExecutions.Select(item => new GoalToolExecutionEvidence(item.ToolName, item.IsError, item.Result)).ToArray(),
                evidence.Validations.Select(item => new GoalValidationEvidence(item.Gate, item.Passed, item.Skipped, item.Summary)).ToArray(),
                evidence.Diagnostics));

    private static void ValidateFencing(GoalRun run, long fencingToken)
    {
        if (fencingToken <= 0 || run.WorkflowFencingToken != fencingToken)
            throw new InvalidOperationException("Stale Goal completion fencing token.");
    }
}
