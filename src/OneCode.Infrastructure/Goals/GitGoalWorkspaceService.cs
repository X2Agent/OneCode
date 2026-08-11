using OneCode.Core.Build;
using OneCode.Core.Commands;
using OneCode.Core.Goals;

namespace OneCode.Infrastructure.Goals;

public sealed class GitGoalWorkspaceService(
    IGitHelper git,
    IWorkspaceFingerprintProvider fingerprintProvider) : IGoalWorkspaceService
{
    public async Task<GoalWorkspaceSnapshot> PrepareAsync(
        GoalRun run,
        CancellationToken ct = default)
    {
        var repositoryRoot = await git.GetRepositoryRootAsync(run.WorkingDirectory, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Goal isolated execution requires a Git repository.");
        var dirtyCount = await git.CountPorcelainChangesAsync(repositoryRoot, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Could not inspect the Goal target workspace.");
        if (dirtyCount != 0)
            throw new InvalidOperationException("Goal isolated execution requires a clean target workspace.");

        var baseCommit = await ReadRequiredAsync(["rev-parse", "HEAD"], repositoryRoot, ct).ConfigureAwait(false);
        var targetBranch = await ReadRequiredAsync(
            ["symbolic-ref", "--quiet", "--short", "HEAD"], repositoryRoot, ct).ConfigureAwait(false);
        var targetFingerprint = await fingerprintProvider.ComputeAsync(repositoryRoot, ct).ConfigureAwait(false);
        var workspaceId = $"goal-{run.Id}";
        var branch = $"onecode/goal/{run.Id}";
        var path = Path.Combine(repositoryRoot, ".onecode", "goal-worktrees", run.Id.Value);

        if (Directory.Exists(path))
        {
            var existingBranch = await ReadRequiredAsync(
                ["symbolic-ref", "--quiet", "--short", "HEAD"], path, ct).ConfigureAwait(false);
            if (!string.Equals(existingBranch, branch, StringComparison.Ordinal))
                throw new InvalidOperationException("Existing Goal worktree belongs to another branch.");
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var created = await git.RunAsync(
                ["worktree", "add", "-b", branch, path, baseCommit], repositoryRoot, ct).ConfigureAwait(false);
            if (created is not { Success: true })
            {
                var attach = await git.RunAsync(
                    ["worktree", "add", path, branch], repositoryRoot, ct).ConfigureAwait(false);
                if (attach is not { Success: true })
                    throw new InvalidOperationException($"Failed to create Goal worktree: {created?.Stderr ?? attach?.Stderr ?? "git unavailable"}");
            }
        }

        return new GoalWorkspaceSnapshot(
            workspaceId,
            repositoryRoot,
            path,
            branch,
            targetBranch,
            baseCommit,
            targetFingerprint,
            DateTimeOffset.UtcNow);
    }

    public async Task<GoalStepReceipt?> FindStepReceiptAsync(
        GoalRun run,
        int goalId,
        long fencingToken,
        CancellationToken ct = default)
    {
        ValidateFencing(run, fencingToken);
        var workspace = RequireWorkspace(run);
        var operationId = BuildStepOperationId(run.Id, goalId);
        var marker = $"OneCode-Operation-Id: {operationId}";
        var commit = await FindPublishedCommitAsync(
            workspace.IsolatedPath, workspace.WorktreeBranch, marker, ct).ConfigureAwait(false);
        if (commit is null)
            return null;
        var evidenceBlob = await ReadRequiredAsync(
            ["show", "-s", "--format=%(trailers:key=OneCode-Evidence-Blob,valueonly)", commit],
            workspace.IsolatedPath,
            ct).ConfigureAwait(false);
        var evidenceJson = await ReadRequiredAsync(
            ["cat-file", "blob", evidenceBlob],
            workspace.IsolatedPath,
            ct).ConfigureAwait(false);
        var evidence = JsonSerializer.Deserialize<GoalStepExecutionEvidence>(evidenceJson)
            ?? throw new InvalidDataException($"Goal step receipt '{commit}' contains no evidence.");
        return new GoalStepReceipt(operationId, goalId, commit, evidence, true, DateTimeOffset.UtcNow);
    }

    public async Task<GoalStepReceipt> RecordStepAsync(
        GoalRun run,
        GoalStepExecutionEvidence evidence,
        long fencingToken,
        CancellationToken ct = default)
    {
        ValidateFencing(run, fencingToken);
        var existing = await FindStepReceiptAsync(run, evidence.GoalId, fencingToken, ct).ConfigureAwait(false);
        if (existing is not null)
            return existing;
        var workspace = RequireWorkspace(run);
        var operationId = BuildStepOperationId(run.Id, evidence.GoalId);
        var temporaryEvidence = Path.Combine(
            Path.GetTempPath(),
            $"onecode-goal-{run.Id}-step-{evidence.GoalId}-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            temporaryEvidence,
            JsonSerializer.Serialize(evidence),
            ct).ConfigureAwait(false);
        string evidenceBlob;
        try
        {
            evidenceBlob = await ReadRequiredAsync(
                ["hash-object", "-w", temporaryEvidence],
                workspace.IsolatedPath,
                ct).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(temporaryEvidence);
        }
        await RunRequiredAsync(["add", "-A"], workspace.IsolatedPath, ct).ConfigureAwait(false);
        await RunRequiredAsync(
            ["commit", "--allow-empty", "-m", $"onecode goal {run.Id} step {evidence.GoalId}\n\nOneCode-Operation-Id: {operationId}\nOneCode-Evidence-Blob: {evidenceBlob}"],
            workspace.IsolatedPath,
            ct).ConfigureAwait(false);
        var commit = await ReadRequiredAsync(["rev-parse", "HEAD"], workspace.IsolatedPath, ct).ConfigureAwait(false);
        return new GoalStepReceipt(operationId, evidence.GoalId, commit, evidence, false, DateTimeOffset.UtcNow);
    }

    public async Task<GoalPublishReceipt> PublishAsync(
        GoalRun run,
        long fencingToken,
        CancellationToken ct = default)
    {
        ValidateFencing(run, fencingToken);
        var workspace = run.Workspace
            ?? throw new InvalidOperationException("GoalRun has no isolated workspace snapshot.");
        var operationId = $"goal/{run.Id}/publish";
        var marker = $"OneCode-Operation-Id: {operationId}";

        var existingReceipt = await FindPublishedCommitAsync(
            workspace.RepositoryRoot, workspace.TargetBranch, marker, ct).ConfigureAwait(false);
        if (existingReceipt is not null)
            return await BuildReceiptAsync(
                operationId,
                existingReceipt,
                workspace.BaseCommit,
                workspace.RepositoryRoot,
                replayed: true,
                ct).ConfigureAwait(false);

        var targetBranch = await ReadRequiredAsync(
            ["symbolic-ref", "--quiet", "--short", "HEAD"], workspace.RepositoryRoot, ct).ConfigureAwait(false);
        if (!string.Equals(targetBranch, workspace.TargetBranch, StringComparison.Ordinal))
            throw new InvalidOperationException("Goal publish target branch changed.");
        var targetHead = await ReadRequiredAsync(["rev-parse", "HEAD"], workspace.RepositoryRoot, ct).ConfigureAwait(false);
        if (!string.Equals(targetHead, workspace.BaseCommit, StringComparison.Ordinal))
            throw new InvalidOperationException("Goal publish target HEAD drifted from the approved baseline.");
        var targetFingerprint = await fingerprintProvider.ComputeAsync(workspace.RepositoryRoot, ct).ConfigureAwait(false);
        if (!string.Equals(targetFingerprint, workspace.TargetWorkspaceFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Goal publish target workspace fingerprint drifted.");

        var changed = await git.CountPorcelainChangesAsync(workspace.IsolatedPath, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Could not inspect the Goal isolated workspace.");
        if (changed != 0)
            throw new InvalidOperationException("Goal isolated workspace contains changes without a step receipt.");

        await RunRequiredAsync(
            ["commit", "--allow-empty", "-m", $"onecode goal {run.Id} publish\n\n{marker}"],
            workspace.IsolatedPath,
            ct).ConfigureAwait(false);
        var sourceCommitsText = await ReadRequiredAsync(
            ["rev-list", "--reverse", $"{workspace.BaseCommit}..HEAD"],
            workspace.IsolatedPath,
            ct).ConfigureAwait(false);
        var sourceCommits = sourceCommitsText.Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (sourceCommits.Length == 0)
            throw new InvalidOperationException("Goal publish found no isolated commits.");
        var cherryPickArguments = new List<string> { "cherry-pick", "--allow-empty" };
        cherryPickArguments.AddRange(sourceCommits);
        var cherryPick = await git.RunAsync(cherryPickArguments.ToArray(), workspace.RepositoryRoot, ct).ConfigureAwait(false);
        if (cherryPick is not { Success: true })
        {
            _ = await git.RunAsync(["cherry-pick", "--abort"], workspace.RepositoryRoot, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException($"Goal publish conflict: {cherryPick?.Stderr ?? "git unavailable"}");
        }
        var publishedCommit = await ReadRequiredAsync(["rev-parse", "HEAD"], workspace.RepositoryRoot, ct).ConfigureAwait(false);
        return await BuildReceiptAsync(
            operationId,
            publishedCommit,
            workspace.BaseCommit,
            workspace.RepositoryRoot,
            replayed: false,
            ct).ConfigureAwait(false);
    }

    public async Task CleanupAsync(GoalRun run, long fencingToken, CancellationToken ct = default)
    {
        ValidateFencing(run, fencingToken);
        if (run.State != GoalRunState.Completed)
            throw new InvalidOperationException("Only a completed GoalRun may remove its isolated workspace.");
        var workspace = run.Workspace;
        if (workspace is null)
            return;
        if (Directory.Exists(workspace.IsolatedPath))
        {
            var removed = await git.RunAsync(
                ["worktree", "remove", workspace.IsolatedPath, "--force"],
                workspace.RepositoryRoot,
                ct).ConfigureAwait(false);
            if (removed is not { Success: true } && Directory.Exists(workspace.IsolatedPath))
                throw new InvalidOperationException($"Failed to remove Goal worktree: {removed?.Stderr ?? "git unavailable"}");
        }
        _ = await git.RunAsync(["branch", "-D", workspace.WorktreeBranch], workspace.RepositoryRoot, ct).ConfigureAwait(false);
    }

    private async Task<GoalPublishReceipt> BuildReceiptAsync(
        string operationId,
        string commit,
        string baseCommit,
        string repositoryRoot,
        bool replayed,
        CancellationToken ct)
    {
        var files = await ReadRequiredAsync(
            ["diff", "--name-only", baseCommit, commit], repositoryRoot, ct).ConfigureAwait(false);
        return new GoalPublishReceipt(
            operationId,
            commit,
            files.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            DateTimeOffset.UtcNow,
            replayed);
    }

    private static string BuildStepOperationId(GoalRunId runId, int goalId)
        => $"goal/{runId}/step/{goalId}";

    private static GoalWorkspaceSnapshot RequireWorkspace(GoalRun run)
        => run.Workspace
            ?? throw new InvalidOperationException("GoalRun has no isolated workspace snapshot.");

    private async Task<string?> FindPublishedCommitAsync(
        string repositoryRoot,
        string targetBranch,
        string marker,
        CancellationToken ct)
    {
        var result = await git.RunAsync(
            ["log", targetBranch, "--fixed-strings", $"--grep={marker}", "--format=%H", "-1"],
            repositoryRoot,
            ct).ConfigureAwait(false);
        if (result is not { Success: true } || string.IsNullOrWhiteSpace(result.Stdout))
            return null;
        return result.Stdout.Trim();
    }

    private async Task<string> ReadRequiredAsync(string[] arguments, string directory, CancellationToken ct)
    {
        var result = await git.RunAsync(arguments, directory, ct).ConfigureAwait(false);
        if (result is not { Success: true } || string.IsNullOrWhiteSpace(result.Stdout))
            throw new InvalidOperationException($"Git command failed: git {string.Join(' ', arguments)} — {result?.Stderr ?? "git unavailable"}");
        return result.Stdout.Trim();
    }

    private async Task RunRequiredAsync(string[] arguments, string directory, CancellationToken ct)
    {
        var result = await git.RunAsync(arguments, directory, ct).ConfigureAwait(false);
        if (result is not { Success: true })
            throw new InvalidOperationException($"Git command failed: git {string.Join(' ', arguments)} — {result?.Stderr ?? "git unavailable"}");
    }

    private static void ValidateFencing(GoalRun run, long fencingToken)
    {
        if (fencingToken <= 0 || run.WorkflowFencingToken != fencingToken)
            throw new InvalidOperationException("Stale Goal workspace fencing token.");
    }
}
