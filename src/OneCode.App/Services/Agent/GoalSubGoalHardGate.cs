using OneCode.App.Services.Lsp;
using OneCode.Core.IO;
using OneCode.Core.Lsp;

namespace OneCode.App.Services.Agent;

/// <summary>
/// W4-A: deterministic hard gates for a sub-goal attempt (verify / LSP / path scope).
/// </summary>
internal sealed class GoalSubGoalHardGate(
    IVerificationProvider? verificationProvider = null,
    LspDiagnosticRegistry? diagnosticRegistry = null)
{
    private readonly IVerificationProvider? _verificationProvider = verificationProvider;
    private readonly LspDiagnosticRegistry? _diagnosticRegistry = diagnosticRegistry;

    internal async Task<SubGoalHardValidationResult> ValidateAsync(
        GoalItem goal,
        string workingDirectory,
        IReadOnlyList<string> changedFiles,
        IReadOnlyList<GoalToolExecutionEvidence> toolExecutions,
        string agentOutput,
        CancellationToken ct)
    {
        var validations = new List<GoalValidationEvidence>();
        var fullWorkingDirectory = Path.GetFullPath(workingDirectory);
        var relativeChangedFiles = changedFiles
            .Select(path => Path.GetRelativePath(fullWorkingDirectory, Path.GetFullPath(path)))
            .ToList();

        var toolErrors = toolExecutions.Where(e => e.IsError).ToList();
        validations.Add(new GoalValidationEvidence(
            "tool-execution",
            toolErrors.Count == 0,
            false,
            toolErrors.Count == 0
                ? $"{toolExecutions.Count} tool execution(s) completed without unresolved errors."
                : $"{toolErrors.Count} tool execution error(s): {string.Join("; ", toolErrors.Select(e => $"{e.ToolName}: {e.Result}"))}"));

        var missingFiles = goal.ExpectedFiles
            .Select(path => GoalSubGoalAssessment.ResolveWorkspacePath(fullWorkingDirectory, path))
            .Where(path => !File.Exists(path) && !Directory.Exists(path))
            .Select(path => Path.GetRelativePath(fullWorkingDirectory, path))
            .ToList();
        validations.Add(new GoalValidationEvidence(
            "expected-artifacts",
            missingFiles.Count == 0,
            goal.ExpectedFiles.Count == 0,
            goal.ExpectedFiles.Count == 0
                ? "No explicit expected artifacts were declared."
                : missingFiles.Count == 0
                    ? $"All {goal.ExpectedFiles.Count} expected artifact(s) exist."
                    : $"Missing expected artifact(s): {string.Join(", ", missingFiles)}"));

        var outOfScope = GoalSubGoalAssessment.FindOutOfScopeFiles(fullWorkingDirectory, changedFiles, goal.AllowedPaths);
        validations.Add(new GoalValidationEvidence(
            "change-scope",
            outOfScope.Count == 0,
            false,
            outOfScope.Count == 0
                ? $"All {changedFiles.Count} changed file(s) are inside the allowed workspace scope."
                : $"Out-of-scope file modification(s): {string.Join(", ", outOfScope)}"));

        var sourceFilesChanged = _verificationProvider is not null
            && changedFiles.Any(_verificationProvider.IsSourceFile);
        var verificationRequired = goal.RequiresBuild || goal.RequiresTests || sourceFilesChanged;
        VerificationResult? verification = null;
        if (verificationRequired)
        {
            verification = _verificationProvider is null
                ? null
                : goal.RequiresTests
                    ? await _verificationProvider.VerifyBuildAndTestsAsync(workingDirectory, changedFiles, ct).ConfigureAwait(false)
                    : await _verificationProvider.VerifyAsync(workingDirectory, changedFiles, ct).ConfigureAwait(false);
            validations.Add(new GoalValidationEvidence(
                goal.RequiresTests ? "build-and-test" : "build",
                verification is { Success: true, Skipped: false },
                false,
                verification?.FormatForLlm() ?? "No verification provider is registered."));
        }
        else
        {
            validations.Add(new GoalValidationEvidence(
                "build",
                true,
                true,
                "No source changes or explicit build/test requirement were detected."));
        }

        var diagnostics = GetRelevantDiagnostics(fullWorkingDirectory, changedFiles);
        validations.Add(new GoalValidationEvidence(
            "static-diagnostics",
            diagnostics.Count == 0,
            _diagnosticRegistry is null,
            _diagnosticRegistry is null
                ? "LSP diagnostic registry is unavailable; compiler verification remains authoritative."
                : diagnostics.Count == 0
                    ? "No unresolved LSP errors were reported for changed files."
                    : $"Unresolved LSP error(s): {string.Join("; ", diagnostics)}"));

        var evidence = new SubGoalEvidence(
            AgentSummary: agentOutput,
            ChangedFiles: relativeChangedFiles,
            ToolExecutions: toolExecutions.ToList(),
            Validations: validations,
            Diagnostics: diagnostics);
        var failedGates = validations.Where(v => !v.Passed && !v.Skipped).ToList();
        return failedGates.Count == 0
            ? new SubGoalHardValidationResult(true, null, evidence)
            : new SubGoalHardValidationResult(
                false,
                "Deterministic validation failed. Fix these issues before claiming completion:\n" +
                string.Join("\n", failedGates.Select(v => $"- {v.Gate}: {v.Summary}")),
                evidence);
    }

    private IReadOnlyList<string> GetRelevantDiagnostics(
        string workingDirectory,
        IReadOnlyList<string> changedFiles)
    {
        if (_diagnosticRegistry is null || changedFiles.Count == 0)
            return [];

        var changed = changedFiles
            .Select(Path.GetFullPath)
            .ToHashSet(Core.IO.PathComparer.Default);
        return _diagnosticRegistry.GetAllDiagnostics()
            .Where(d => d.Severity == LspDiagnosticSeverity.Error)
            .Where(d => PathBoundary.IsWithinDirectory(d.FilePath, workingDirectory))
            .Where(d => changed.Contains(Path.GetFullPath(d.FilePath)))
            .Select(d => d.Summary)
            .ToList();
    }

    internal sealed record SubGoalHardValidationResult(
        bool Passed,
        string? Feedback,
        SubGoalEvidence Evidence);
}
