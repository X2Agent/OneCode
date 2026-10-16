using System.Diagnostics;
using OneCode.Core.IO;

namespace OneCode.Core.Goals;

/// <summary>
/// Resolves effective working directories and step file paths inside an isolated Goal worktree,
/// preserving any relative subdirectory offset from the original repository root.
/// </summary>
public static class GoalWorkspacePathResolver
{
    /// <summary>
    /// Computes the effective working directory for a <see cref="GoalRun"/>.
    /// If the user started the Goal inside a subdirectory of <see cref="GoalWorkspaceSnapshot.RepositoryRoot"/>
    /// (for example <c>.dev-workspace</c>), maps that same relative offset under
    /// <see cref="GoalWorkspaceSnapshot.IsolatedPath"/> and ensures the directory exists.
    /// </summary>
    public static string ResolveEffectiveWorkingDirectory(GoalRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var (isolatedRoot, mapped) = ResolveEffectiveDirectories(run);
        if (isolatedRoot is null)
            return run.WorkingDirectory;

        if (!string.Equals(mapped, isolatedRoot, PathBoundary.DefaultComparison)
            && Directory.Exists(isolatedRoot)
            && !Directory.Exists(mapped))
        {
            Directory.CreateDirectory(mapped);
        }

        return mapped;
    }

    /// <summary>
    /// Resolves a step-reported file path (whether rooted, relative to the effective subdirectory,
    /// or relative to the worktree root) into a normalized absolute path inside the isolated worktree.
    /// </summary>
    public static string ResolveWorktreeFilePath(GoalRun run, string path)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        var (isolatedRoot, effectiveDir) = ResolveEffectiveDirectories(run);
        if (isolatedRoot is null)
        {
            var baseDir = string.IsNullOrWhiteSpace(run.WorkingDirectory)
                ? Directory.GetCurrentDirectory()
                : run.WorkingDirectory;
            return Path.GetFullPath(path, baseDir);
        }

        if (string.Equals(effectiveDir, isolatedRoot, PathBoundary.DefaultComparison))
            return Path.GetFullPath(path, isolatedRoot);

        var subDirRel = Path.GetRelativePath(isolatedRoot, effectiveDir).Replace('\\', '/');
        var normalizedInput = path.Replace('\\', '/').TrimStart('/');
        if (string.Equals(normalizedInput, subDirRel, PathBoundary.DefaultComparison)
            || normalizedInput.StartsWith(subDirRel + "/", PathBoundary.DefaultComparison))
        {
            return Path.GetFullPath(path, isolatedRoot);
        }

        var fromEffective = Path.GetFullPath(path, effectiveDir);
        var fromRoot = Path.GetFullPath(path, isolatedRoot);
        if (Directory.Exists(isolatedRoot) && !File.Exists(fromEffective) && File.Exists(fromRoot))
            return fromRoot;

        return fromEffective;
    }

    /// <summary>
    /// Resolves a step-reported file path to a forward-slash path relative to
    /// <see cref="GoalWorkspaceSnapshot.IsolatedPath"/>, or <see langword="null"/> if the path
    /// is empty or escapes the isolated worktree boundary.
    /// </summary>
    public static string? ResolveWorktreeRelativePath(GoalRun run, string path)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var (isolatedRoot, _) = ResolveEffectiveDirectories(run);
        if (isolatedRoot is null)
            return null;

        string fullPath;
        try
        {
            fullPath = ResolveWorktreeFilePath(run, path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GoalWorkspacePathResolver.ResolveWorktreeRelativePath failed for '{path}': {ex.Message}");
            return null;
        }

        if (!PathBoundary.IsWithinDirectory(fullPath, isolatedRoot))
            return null;

        var rel = Path.GetRelativePath(isolatedRoot, fullPath).Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(rel) || rel == "." || rel.StartsWith("..", StringComparison.Ordinal))
            return null;

        return rel;
    }

    private static (string? IsolatedRoot, string EffectiveDirectory) ResolveEffectiveDirectories(GoalRun run)
    {
        if (run.Workspace is not { } workspace || string.IsNullOrWhiteSpace(workspace.IsolatedPath))
            return (null, run.WorkingDirectory);

        var isolatedRoot = Path.GetFullPath(workspace.IsolatedPath);
        if (string.IsNullOrWhiteSpace(workspace.RepositoryRoot) || string.IsNullOrWhiteSpace(run.WorkingDirectory))
            return (isolatedRoot, isolatedRoot);

        try
        {
            var repoRoot = Path.GetFullPath(workspace.RepositoryRoot);
            var originalCwd = Path.GetFullPath(run.WorkingDirectory);

            if (!PathBoundary.IsWithinDirectory(originalCwd, repoRoot))
                return (isolatedRoot, isolatedRoot);

            var relative = Path.GetRelativePath(repoRoot, originalCwd);
            if (string.IsNullOrEmpty(relative) || relative == "." || relative.StartsWith("..", StringComparison.Ordinal))
                return (isolatedRoot, isolatedRoot);

            var mapped = Path.GetFullPath(Path.Combine(isolatedRoot, relative));
            if (!PathBoundary.IsWithinDirectory(mapped, isolatedRoot))
                return (isolatedRoot, isolatedRoot);

            return (isolatedRoot, mapped);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GoalWorkspacePathResolver.ResolveEffectiveDirectories fallback: {ex.Message}");
            return (isolatedRoot, isolatedRoot);
        }
    }
}
