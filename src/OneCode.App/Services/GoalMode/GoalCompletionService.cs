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
    LspDiagnosticRegistry? diagnosticRegistry = null,
    TimeSpan? diagnosticQuietPeriod = null,
    TimeSpan? diagnosticTimeout = null) : IGoalCompletionService
{
    // Fix-3：读取诊断前等待 quiescent——连续 quietPeriod 无新 Error 视为稳定，上限 timeout。
    private readonly TimeSpan _diagnosticQuietPeriod = diagnosticQuietPeriod ?? TimeSpan.FromSeconds(2);
    private readonly TimeSpan _diagnosticTimeout = diagnosticTimeout ?? TimeSpan.FromSeconds(30);

    public async Task<GoalRun> CompleteAsync(GoalRun run, long fencingToken, CancellationToken ct)
    {
        ValidateFencing(run, fencingToken);
        var current = await ReloadAsync(run.Id, ct).ConfigureAwait(false);
        ValidateFencing(current, fencingToken);
        if (current.IsTerminal)
            return current;
        // Fix-1 最小改动路径：budget 终止的 Paused Run 已带终态语义，直接短路返回，
        // 不允许落入下方 FailAsync 被误判为完整性失败。
        if (current.State == GoalRunState.Paused)
            return current;
        if (current.State is not (GoalRunState.Executing or GoalRunState.Validating or GoalRunState.Publishing))
            throw new InvalidOperationException($"GoalRun '{current.Id}' cannot complete from state '{current.State}'.");

        var report = new List<GoalGateEvidence>();
        // Fix-1/F-03：预算耗尽跳过的必需步骤不是完整性失败——保持 Paused 终态并输出汇总报告，
        // 用户可追加预算后通过 /resume-goal 续跑，而不是被判 Failed。
        var budgetSkipped = current.Plan
            .Where(step => !step.Optional && IsBudgetSkipped(step, current.Executions))
            .ToArray();
        if (budgetSkipped.Length > 0)
        {
            report.Add(new GoalGateEvidence(
                "state-integrity",
                false,
                true,
                $"budget-exhausted: required sub-goals skipped due to exhausted budget: "
                    + $"{string.Join(", ", budgetSkipped.Select(step => $"#{step.Id}"))}. "
                    + "Run kept Paused; add budget and resume to continue."));
            var paused = current with
            {
                State = GoalRunState.Paused,
                TerminalReason = BuildTerminalReason.BudgetExceeded,
                FailureSummary = "Goal execution paused: budget exhausted before completing required sub-goal(s).",
                FinalValidation = report,
            };
            await SaveAsync(paused, fencingToken, ct).ConfigureAwait(false);
            return await ReloadAsync(current.Id, ct).ConfigureAwait(false);
        }

        if (current.State == GoalRunState.Publishing)
            return await PublishAsync(current, fencingToken, ct).ConfigureAwait(false);

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
            // Fix-4/F-09：路径比较平台化——Windows 大小写不敏感，Unix 敏感。
            .Distinct(PathComparer.Default)
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

        // Fix-3/F-08：读取诊断前等待 quiescent，防止诊断异步推送未到位造成假阳性通过；
        // 若在超时内仍未稳定，则将该 gate 标记 Skipped（与 registry 缺失同语义），
        // 既不误杀也不假通过。
        GoalGateEvidence diagnosticsGate;
        if (diagnosticRegistry is null || changedFiles.Length == 0)
        {
            diagnosticsGate = new GoalGateEvidence(
                "static-diagnostics",
                true,
                true,
                "LSP diagnostics unavailable; build/test evidence remains authoritative.");
        }
        else
        {
            var changed = changedFiles.Select(Path.GetFullPath).ToHashSet(PathComparer.Default);
            var stabilized = await WaitForDiagnosticQuiescenceAsync(changed, ct).ConfigureAwait(false);
            var diagnostics = stabilized
                ? FilterErrorDiagnostics(workspace.IsolatedPath, changed)
                : [];
            diagnosticsGate = new GoalGateEvidence(
                "static-diagnostics",
                diagnostics.Count == 0,
                !stabilized,
                !stabilized
                    ? "LSP diagnostics did not stabilize before the timeout; gate skipped to avoid a false result."
                    : diagnostics.Count == 0
                        ? "No unresolved LSP errors were reported for changed files."
                        : string.Join("; ", diagnostics));
        }
        report.Add(diagnosticsGate);
        if (!diagnosticsGate.Passed && !diagnosticsGate.Skipped)
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

    private async Task<bool> WaitForDiagnosticQuiescenceAsync(IReadOnlySet<string> changedFiles, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + _diagnosticTimeout;
        var signature = DiagnosticsSignature(changedFiles);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(_diagnosticQuietPeriod, ct).ConfigureAwait(false);
            var next = DiagnosticsSignature(changedFiles);
            if (next == signature)
                return true;
            signature = next;
        }
        return false;
    }

    private IReadOnlyList<string> FilterErrorDiagnostics(string workingDirectory, IReadOnlySet<string> changedFiles)
        => diagnosticRegistry!.GetAllDiagnostics()
            .Where(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error)
            .Where(diagnostic => changedFiles.Contains(Path.GetFullPath(diagnostic.FilePath)))
            .Where(diagnostic => PathBoundary.IsWithinDirectory(diagnostic.FilePath, workingDirectory))
            .Select(diagnostic => diagnostic.Summary)
            .ToArray();

    private string DiagnosticsSignature(IReadOnlySet<string> changedFiles)
        => string.Join("|", FilterUnscopedErrorDiagnostics(changedFiles)
            .Select(diagnostic => diagnostic.Summary)
            .OrderBy(static summary => summary, StringComparer.Ordinal));

    private IReadOnlyList<LspDiagnostic> FilterUnscopedErrorDiagnostics(IReadOnlySet<string> changedFiles)
        => diagnosticRegistry!.GetAllDiagnostics()
            .Where(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error)
            .Where(diagnostic => changedFiles.Contains(Path.GetFullPath(diagnostic.FilePath)))
            .ToArray();

    private static bool IsBudgetSkipped(GoalStepSnapshot step, IReadOnlyList<GoalStepExecutionEvidence> executions)
    {
        if (step.State != GoalStepState.Skipped)
            return false;
        var last = executions.LastOrDefault(evidence => evidence.GoalId == step.Id);
        return last is not null
            && last.State == GoalStepState.Skipped
            && last.Validations.Any(gate => gate.Gate == "budget" && gate.Skipped);
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
            // F-10：保留 Skipped/InProgress/Pending 保真映射，非 Completed 不再一律降级 Failed，
            // 提升 final-semantic-review 的输入保真度。
            evidence.State switch
            {
                GoalStepState.Completed => GoalStatus.Completed,
                GoalStepState.Skipped => GoalStatus.Skipped,
                GoalStepState.InProgress => GoalStatus.InProgress,
                GoalStepState.Pending => GoalStatus.Pending,
                _ => GoalStatus.Failed,
            },
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
