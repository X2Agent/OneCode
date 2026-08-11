using OneCode.App.Services.Lsp;
using OneCode.Core.Coordinator;
using OneCode.Core.Lsp;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Coordinator;

public sealed record TeamQualityGateContext(
    string WorkingDirectory,
    IReadOnlyList<string> ModifiedFiles,
    TeamRun Run);

public interface ITeamQualityGateValidator
{
    QualityGateKind Kind { get; }

    Task<QualityGateResult> ValidateAsync(
        QualityGateDefinition definition,
        TeamQualityGateContext context,
        CancellationToken ct);
}

public sealed class TeamQualityGateRunner(IEnumerable<ITeamQualityGateValidator> validators)
{
    private static readonly IReadOnlyDictionary<QualityGateKind, int> s_executionOrder =
        new Dictionary<QualityGateKind, int>
        {
            [QualityGateKind.ChangeScope] = 0,
            [QualityGateKind.WorkspaceCleanliness] = 1,
            [QualityGateKind.LspDiagnostics] = 2,
            [QualityGateKind.Build] = 3,
            [QualityGateKind.UnitTest] = 4,
            [QualityGateKind.IntegrationTest] = 5,
            [QualityGateKind.AcceptanceCriteria] = 6,
            [QualityGateKind.Security] = 7,
        };

    private readonly IReadOnlyDictionary<QualityGateKind, ITeamQualityGateValidator> _validators =
        validators.ToDictionary(validator => validator.Kind);

    public async Task<IReadOnlyList<QualityGateResult>> RunAsync(
        IReadOnlyList<QualityGateDefinition> definitions,
        string workingDirectory,
        EditTransaction transaction,
        TeamRun run,
        CancellationToken ct)
    {
        var context = new TeamQualityGateContext(
            workingDirectory,
            transaction.GetModifiedFiles(),
            run);
        var ordered = definitions
            .Select((definition, index) => (definition, index))
            .OrderBy(item => s_executionOrder.GetValueOrDefault(item.definition.Kind, int.MaxValue))
            .ThenBy(item => item.index)
            .Select(item => item.definition)
            .ToList();

        var results = new List<QualityGateResult>(ordered.Count);
        foreach (var definition in ordered)
        {
            QualityGateResult result;
            if (!_validators.TryGetValue(definition.Kind, out var validator))
            {
                result = new QualityGateResult(
                    definition.Id,
                    definition.Kind,
                    definition.Required,
                    QualityGateStatus.SkippedByDependency,
                    $"No deterministic {definition.Kind} gate validator is registered.",
                    [],
                    TimeSpan.Zero);
            }
            else
            {
                result = await validator.ValidateAsync(definition, context, ct).ConfigureAwait(false);
            }

            results.Add(result);
            if (result.Status != QualityGateStatus.Passed && definition.Required)
            {
                foreach (var remaining in ordered.Skip(results.Count))
                {
                    results.Add(new QualityGateResult(
                        remaining.Id,
                        remaining.Kind,
                        remaining.Required,
                        QualityGateStatus.SkippedByDependency,
                        $"Skipped because required gate '{definition.Id}' failed.",
                        [],
                        TimeSpan.Zero));
                }
                break;
            }
        }

        return results;
    }
}

public sealed class TeamChangeScopeQualityGateValidator : ITeamQualityGateValidator
{
    public QualityGateKind Kind => QualityGateKind.ChangeScope;

    public Task<QualityGateResult> ValidateAsync(
        QualityGateDefinition definition,
        TeamQualityGateContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var allowed = (context.Run.Plan?.Tasks ?? [])
            .SelectMany(task => task.AllowedPaths ?? [])
            .Select(path => Normalize(context.WorkingDirectory, path))
            .ToArray();
        var violations = context.ModifiedFiles
            .Select(path => Normalize(context.WorkingDirectory, path))
            .Where(path => allowed.Length == 0
                || !allowed.Any(root => IsUnder(path, root)))
            .ToArray();
        var passed = violations.Length == 0;
        return Task.FromResult(new QualityGateResult(
            definition.Id,
            definition.Kind,
            definition.Required,
            passed ? QualityGateStatus.Passed : QualityGateStatus.Failed,
            passed
                ? "All Team changes are within the approved task scope."
                : $"Out-of-scope Team changes: {string.Join(", ", violations)}",
            violations,
            TimeSpan.Zero));
    }

    private static string Normalize(string root, string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));

    private static bool IsUnder(string path, string root)
        => string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
}

public sealed class TeamWorkspaceCleanlinessQualityGateValidator : ITeamQualityGateValidator
{
    public QualityGateKind Kind => QualityGateKind.WorkspaceCleanliness;

    public Task<QualityGateResult> ValidateAsync(
        QualityGateDefinition definition,
        TeamQualityGateContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var workingDirectoryExists = Directory.Exists(context.WorkingDirectory);
        var passed = workingDirectoryExists
            && string.Equals(
                Path.GetFullPath(context.WorkingDirectory),
                Path.GetFullPath(context.Run.WorkingDirectory),
                StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new QualityGateResult(
            definition.Id,
            definition.Kind,
            definition.Required,
            passed ? QualityGateStatus.Passed : QualityGateStatus.Failed,
            passed
                ? "Team workspace identity is unchanged."
                : "Team workspace is missing or differs from the approved run workspace.",
            passed ? [] : [context.WorkingDirectory, context.Run.WorkingDirectory],
            TimeSpan.Zero));
    }
}

public sealed class TeamSecurityQualityGateValidator : ITeamQualityGateValidator
{
    public QualityGateKind Kind => QualityGateKind.Security;

    public Task<QualityGateResult> ValidateAsync(
        QualityGateDefinition definition,
        TeamQualityGateContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sensitiveFiles = context.ModifiedFiles
            .Where(path => Path.GetFileName(path).StartsWith(".env", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path) is ".pem" or ".key" or ".pfx")
            .ToArray();
        var passed = sensitiveFiles.Length == 0;
        return Task.FromResult(new QualityGateResult(
            definition.Id,
            definition.Kind,
            definition.Required,
            passed ? QualityGateStatus.Passed : QualityGateStatus.Failed,
            passed
                ? "No private-key or environment-secret files were modified."
                : $"Sensitive files require explicit security review: {string.Join(", ", sensitiveFiles)}",
            sensitiveFiles,
            TimeSpan.Zero));
    }
}

public sealed class TeamBuildQualityGateValidator(IVerificationProvider verificationProvider)
    : VerificationQualityGateValidator(verificationProvider)
{
    public override QualityGateKind Kind => QualityGateKind.Build;

    protected override Task<VerificationResult> VerifyAsync(
        TeamQualityGateContext context,
        CancellationToken ct)
        => VerificationProvider.VerifyAsync(
            context.WorkingDirectory,
            context.ModifiedFiles,
            ct);
}

public sealed class TeamUnitTestQualityGateValidator(IVerificationProvider verificationProvider)
    : VerificationQualityGateValidator(verificationProvider)
{
    public override QualityGateKind Kind => QualityGateKind.UnitTest;

    protected override Task<VerificationResult> VerifyAsync(
        TeamQualityGateContext context,
        CancellationToken ct)
        => VerificationProvider.VerifyTestsAsync(
            context.WorkingDirectory,
            context.ModifiedFiles,
            ct);
}

public sealed class TeamIntegrationTestQualityGateValidator(IVerificationProvider verificationProvider)
    : VerificationQualityGateValidator(verificationProvider)
{
    public override QualityGateKind Kind => QualityGateKind.IntegrationTest;

    protected override Task<VerificationResult> VerifyAsync(
        TeamQualityGateContext context,
        CancellationToken ct)
        => VerificationProvider.VerifyIntegrationTestsAsync(
            context.WorkingDirectory,
            context.ModifiedFiles,
            ct);
}

public abstract class VerificationQualityGateValidator(IVerificationProvider verificationProvider)
    : ITeamQualityGateValidator
{
    protected IVerificationProvider VerificationProvider { get; } = verificationProvider;

    public abstract QualityGateKind Kind { get; }

    public async Task<QualityGateResult> ValidateAsync(
        QualityGateDefinition definition,
        TeamQualityGateContext context,
        CancellationToken ct)
    {
        var verification = await VerifyAsync(context, ct).ConfigureAwait(false);
        var passed = verification.Success && !verification.Skipped;
        var evidence = verification.Errors.Select(error => error.ToString()).ToList();
        if (verification.Skipped)
        {
            evidence.Add(
                $"workingDirectory={context.WorkingDirectory}; modifiedFiles={context.ModifiedFiles.Count}; " +
                "no matching project profile or build tool was available.");
        }
        return new QualityGateResult(
            definition.Id,
            definition.Kind,
            definition.Required,
            passed ? QualityGateStatus.Passed : QualityGateStatus.Failed,
            verification.FormatForLlm(),
            evidence,
            verification.Duration);
    }

    protected abstract Task<VerificationResult> VerifyAsync(
        TeamQualityGateContext context,
        CancellationToken ct);
}

public sealed class TeamLspDiagnosticsQualityGateValidator(LspDiagnosticRegistry diagnostics)
    : ITeamQualityGateValidator
{
    public QualityGateKind Kind => QualityGateKind.LspDiagnostics;

    public Task<QualityGateResult> ValidateAsync(
        QualityGateDefinition definition,
        TeamQualityGateContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var modifiedPaths = context.ModifiedFiles
            .Select(path => NormalizePath(context.WorkingDirectory, path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var errors = diagnostics.GetAllDiagnostics()
            .Where(diagnostic => diagnostic.Severity == LspDiagnosticSeverity.Error)
            .Where(diagnostic => modifiedPaths.Count == 0
                || modifiedPaths.Contains(NormalizePath(context.WorkingDirectory, diagnostic.FilePath)))
            .Select(diagnostic => diagnostic.Summary)
            .ToList();
        var passed = errors.Count == 0;
        return Task.FromResult(new QualityGateResult(
            definition.Id,
            definition.Kind,
            definition.Required,
            passed ? QualityGateStatus.Passed : QualityGateStatus.Failed,
            passed ? "No LSP error diagnostics were reported for modified files." : $"LSP reported {errors.Count} error diagnostic(s).",
            errors,
            TimeSpan.Zero));
    }

    private static string NormalizePath(string workingDirectory, string path)
    {
        try
        {
            return Path.GetFullPath(Path.IsPathRooted(path)
                ? path
                : Path.Combine(workingDirectory, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}

public sealed class TeamAcceptanceCriteriaQualityGateValidator : ITeamQualityGateValidator
{
    public QualityGateKind Kind => QualityGateKind.AcceptanceCriteria;

    public Task<QualityGateResult> ValidateAsync(
        QualityGateDefinition definition,
        TeamQualityGateContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requiredTasks = context.Run.TaskGraph?.RequiredTasks ?? [];
        var failures = requiredTasks
            .Where(task => task.Status != TeamTaskStatus.Succeeded
                || task.Definition.AcceptanceCriteria.Count == 0
                || string.IsNullOrWhiteSpace(task.Summary))
            .Select(task => $"{task.Definition.Id}: status={task.Status}, criteria={task.Definition.AcceptanceCriteria.Count}, evidence={(string.IsNullOrWhiteSpace(task.Summary) ? "missing" : "present")}")
            .ToList();
        var evidence = requiredTasks
            .Where(task => task.Status == TeamTaskStatus.Succeeded && !string.IsNullOrWhiteSpace(task.Summary))
            .Select(task => $"{task.Definition.Id}: {task.Summary}")
            .ToList();
        var passed = requiredTasks.Count > 0 && failures.Count == 0;
        return Task.FromResult(new QualityGateResult(
            definition.Id,
            definition.Kind,
            definition.Required,
            passed ? QualityGateStatus.Passed : QualityGateStatus.Failed,
            passed
                ? $"Acceptance evidence exists for {requiredTasks.Count} required task(s)."
                : "One or more required tasks lack successful acceptance evidence.",
            passed ? evidence : failures,
            TimeSpan.Zero));
    }
}

public sealed class DeliveryReportBuilder
{
    public DeliveryReport Build(TeamRun run, bool committed, string summary)
    {
        if (run.TaskGraph is null)
            throw new InvalidOperationException("DeliveryReport requires a TeamTaskGraph.");

        return new DeliveryReport(
            run.Id,
            run.TeamName,
            committed,
            summary,
            run.TaskGraph.Tasks,
            run.GateResults,
            run.Changes,
            run.Plan?.Risks ?? [],
            DateTimeOffset.UtcNow);
    }
}
