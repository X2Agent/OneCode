using OneCode.Core.Build;
using OneCode.Core.Commands;
using OneCode.Core.Goals;
using OneCode.Core.IO;
using OneCode.Infrastructure.Git;

namespace OneCode.Infrastructure.Goals;

public sealed class GitGoalWorkspaceService(
    IGitHelper git,
    IWorkspaceFingerprintProvider fingerprintProvider) : IGoalWorkspaceService
{
    // 8: enough to show the dirty set without dumping a large status listing into the TUI error line.
    private const int DirtyPathSampleLimit = 8;

    public async Task<GoalWorkspaceSnapshot> PrepareAsync(
        GoalRun run,
        CancellationToken ct = default)
    {
        var repositoryRoot = await git.GetRepositoryRootAsync(run.WorkingDirectory, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Goal isolated execution requires a Git repository.");
        var dirtyCount = await git.CountPorcelainChangesAsync(repositoryRoot, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Could not inspect the Goal target workspace.");
        if (dirtyCount != 0)
        {
            var status = await git.RunAsync(["status", "--porcelain"], repositoryRoot, ct).ConfigureAwait(false);
            throw CreateDirtyWorkspaceException(
                run.WorkingDirectory, repositoryRoot, dirtyCount, status?.Stdout);
        }

        var baseCommit = await ReadRequiredAsync(["rev-parse", "HEAD"], repositoryRoot, ct).ConfigureAwait(false);
        var targetBranch = await ReadRequiredAsync(
            ["symbolic-ref", "--quiet", "--short", "HEAD"], repositoryRoot, ct).ConfigureAwait(false);
        var targetFingerprint = await fingerprintProvider.ComputeAsync(repositoryRoot, ct).ConfigureAwait(false);
        var workspaceId = $"goal-{run.Id}";
        // Goal 描述生成可读 slug；纯中文/无 ASCII token 时回退到短 id，保证跨平台安全。
        // run.Id 真实为 32 位 GUID（GoalRunId.New()），此处按长度防御（测试/旧数据用短值）。
        var runShortId = run.Id.Value.Length >= 8 ? run.Id.Value[..8] : run.Id.Value;
        var slug = WorktreeLayout.BuildGoalSlug(run.Goal) ?? $"goal-{runShortId}";
        // 跨 run 碰撞消歧：同名 worktree 目录已存在（属于更早的 run）时追加短 id。
        if (Directory.Exists(WorktreeLayout.GetWorktreePath(repositoryRoot, slug)))
            slug = $"{slug}-{runShortId}";

        var branch = $"onecode/goal/{slug}";
        var path = WorktreeLayout.GetWorktreePath(repositoryRoot, slug);

        if (Directory.Exists(path))
        {
            // 恢复场景：worktree 已存在（本次 run 或同 slug 的历史 run），校验分支一致。
            var existingBranch = await ReadRequiredAsync(
                ["symbolic-ref", "--quiet", "--short", "HEAD"], path, ct).ConfigureAwait(false);
            if (!string.Equals(existingBranch, branch, StringComparison.Ordinal))
                throw new InvalidOperationException("Existing Goal worktree belongs to another branch.");
        }
        else
        {
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
                run,
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
            run,
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
        GoalRun run,
        string operationId,
        string commit,
        string baseCommit,
        string repositoryRoot,
        bool replayed,
        CancellationToken ct)
    {
        var diffResult = await git.RunAsync(
            ["diff", "--name-only", baseCommit, commit], repositoryRoot, ct).ConfigureAwait(false);
        if (diffResult is not { Success: true })
            throw new InvalidOperationException($"Git command failed: git diff --name-only {baseCommit} {commit} — {diffResult?.Stderr ?? "git unavailable"}");

        var changedFiles = (diffResult.Stdout ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        SyncUntrackedStepDeliverables(run, repositoryRoot, replayed, changedFiles);

        return new GoalPublishReceipt(
            operationId,
            commit,
            changedFiles,
            DateTimeOffset.UtcNow,
            replayed);
    }

    /// <summary>
    /// 将被 <c>.gitignore</c> 忽略（未进入 Git commit）但由已完成子目标实际产出的工作树文件
    /// 从隔离 worktree 同步回主仓库对应相对路径，避免 <see cref="CleanupAsync"/> 移除 worktree 后产物丢失。
    /// </summary>
    private static void SyncUntrackedStepDeliverables(
        GoalRun run,
        string repositoryRoot,
        bool replayed,
        List<string> committedFiles)
    {
        if (run.Workspace is not { } workspace || string.IsNullOrWhiteSpace(workspace.IsolatedPath))
            return;

        var known = new HashSet<string>(committedFiles, StringComparer.OrdinalIgnoreCase);
        var stepRelativePaths = run.Executions
            .Where(e => e.State == GoalStepState.Completed)
            .SelectMany(e => e.ChangedFiles)
            .Select(path => GoalWorkspacePathResolver.ResolveWorktreeRelativePath(run, path))
            .Where(rel => !string.IsNullOrWhiteSpace(rel))
            .Select(rel => rel!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var rel in stepRelativePaths)
        {
            if (known.Contains(rel))
                continue;

            var osRel = rel.Replace('/', Path.DirectorySeparatorChar);
            var sourcePath = Path.GetFullPath(Path.Combine(workspace.IsolatedPath, osRel));
            var targetPath = Path.GetFullPath(Path.Combine(repositoryRoot, osRel));
            if (!PathBoundary.IsWithinDirectory(sourcePath, workspace.IsolatedPath)
                || !PathBoundary.IsWithinDirectory(targetPath, repositoryRoot))
            {
                continue;
            }

            if (!replayed && File.Exists(sourcePath))
            {
                if (!string.Equals(sourcePath, targetPath, PathBoundary.DefaultComparison))
                {
                    var parentDir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(parentDir))
                        Directory.CreateDirectory(parentDir);
                    File.Copy(sourcePath, targetPath, overwrite: true);
                }

                known.Add(rel);
                committedFiles.Add(rel);
            }
            else if (replayed && File.Exists(targetPath))
            {
                known.Add(rel);
                committedFiles.Add(rel);
            }
        }
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

    private static InvalidOperationException CreateDirtyWorkspaceException(
        string workingDirectory,
        string repositoryRoot,
        int dirtyCount,
        string? porcelain)
    {
        var samples = ParsePorcelainPaths(porcelain).Take(DirtyPathSampleLimit).ToList();
        var sampleText = samples.Count == 0
            ? string.Empty
            : " Examples: " + string.Join(", ", samples) + (dirtyCount > samples.Count ? ", …" : ".");

        var sameDirectory = string.Equals(
            Path.GetFullPath(workingDirectory),
            Path.GetFullPath(repositoryRoot),
            StringComparison.OrdinalIgnoreCase);
        var location = sameDirectory
            ? $"Checked Git repository '{repositoryRoot}'."
            : $"Checked Git repository root '{repositoryRoot}' (session working directory is '{workingDirectory}').";

        return new InvalidOperationException(
            "Goal isolated execution requires a clean Git working tree. "
            + $"{location} Found {dirtyCount} dirty path(s).{sampleText} "
            + "Commit, stash, or discard those changes, then retry Goal mode.");
    }

    private static IEnumerable<string> ParsePorcelainPaths(string? porcelain)
    {
        if (string.IsNullOrWhiteSpace(porcelain))
            yield break;

        foreach (var rawLine in porcelain.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var raw = rawLine.TrimEnd();
            if (raw.Length == 0)
                continue;
            // Porcelain: two-char XY status, space, path. Do not trim the prefix;
            // a worktree-only modify is " M path" and trimming would drop the path head.
            var path = raw.Length >= 4 && raw[2] == ' '
                ? raw[3..]
                : raw.Trim();
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
                path = path[(arrow + 4)..].Trim();
            if (path.Length > 0)
                yield return path;
        }
    }

    private static void ValidateFencing(GoalRun run, long fencingToken)
    {
        if (fencingToken <= 0 || run.WorkflowFencingToken != fencingToken)
            throw new InvalidOperationException("Stale Goal workspace fencing token.");
    }
}
