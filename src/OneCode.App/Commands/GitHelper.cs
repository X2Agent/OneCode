using OneCode.Infrastructure.Abstractions;

namespace OneCode.App.Commands;

/// <summary>
/// Git 辅助服务。Registered as Singleton in DI.
/// </summary>
public sealed class GitHelper : IGitHelper
{
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<GitHelper>? _logger;

    public GitHelper(IProcessRunner processRunner, ILogger<GitHelper>? logger = null)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _processRunner.ExecuteAsync("git", ["--version"], ct: ct).ConfigureAwait(false);
            return result is not null && result.Success ? result.Stdout.Trim() : null;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "GitHelper.GetVersionAsync failed");
            return null;
        }
    }

    /// <inheritdoc />
    public Task<GitCommandResult?> RunAsync(string[] arguments, CancellationToken ct = default)
        => RunAsync(arguments, workingDirectory: null, ct);

    /// <inheritdoc />
    public async Task<GitCommandResult?> RunAsync(
        string[] arguments,
        string? workingDirectory,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _processRunner
                .ExecuteWithArgumentListAsync("git", arguments, workingDirectory, ct: ct)
                .ConfigureAwait(false);
            return result is null
                ? null
                : new GitCommandResult(result.Success, result.Stdout, result.Stderr);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "GitHelper.RunAsync failed");
            return null;
        }
    }

    /// <inheritdoc />
    public Task<string> ReadAsync(string[] arguments, CancellationToken ct = default)
        => ReadAsync(arguments, workingDirectory: null, ct);

    /// <inheritdoc />
    public async Task<string> ReadAsync(
        string[] arguments,
        string? workingDirectory,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _processRunner
                .ExecuteWithArgumentListAsync("git", arguments, workingDirectory, ct: ct)
                .ConfigureAwait(false);

            if (result is null)
                return "(git not available)";

            var output = result.Success
                ? result.Stdout
                : string.IsNullOrWhiteSpace(result.Stderr)
                    ? result.Stdout
                    : result.Stderr;

            return string.IsNullOrWhiteSpace(output)
                ? "(empty)"
                : output.Trim();
        }
        catch (Exception ex)
        {
            return $"(error: {ex.Message})";
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetRepositoryRootAsync(string workingDirectory, CancellationToken ct = default)
    {
        var result = await RunAsync(["rev-parse", "--show-toplevel"], workingDirectory, ct)
            .ConfigureAwait(false);
        if (result is null || !result.Success)
            return null;
        var root = result.Stdout.Trim();
        return string.IsNullOrEmpty(root) ? null : root;
    }

    /// <inheritdoc />
    public async Task<int?> CountPorcelainChangesAsync(string workingDirectory, CancellationToken ct = default)
    {
        var result = await RunAsync(["status", "--porcelain"], workingDirectory, ct)
            .ConfigureAwait(false);
        if (result is null || !result.Success)
            return null;
        return result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewFileEntry>> GetPendingDiffStatAsync(
        CancellationToken ct = default,
        string? workingDirectory = null)
    {
        // --numstat alone: outputs "added\tremoved\tpath" per line (paths relative to repo root).
        // --stat is intentionally omitted — it appends a human-readable summary table
        // that the parser would need to skip, and provides no value here.
        // Run from repo root so subsequent pathspec lookups stay consistent.
        var root = await ResolveGitRootAsync(workingDirectory, ct).ConfigureAwait(false);
        var result = await RunAsync(
                ["-c", "core.quotepath=false", "diff", "--numstat", "HEAD"],
                root,
                ct)
            .ConfigureAwait(false);
        if (result is null || !result.Success)
            return [];

        List<ReviewFileEntry> entries = [];
        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3
                && int.TryParse(parts[0], out var added)
                && int.TryParse(parts[1], out var removed))
            {
                // git --numstat renders renamed files as "src/{old => new}/file.cs".
                // The {old => new} syntax is NOT a valid pathspec for subsequent
                // `git diff -- <path>` calls — it would return empty output.
                // Resolve to the actual new path so the diff lookup works.
                var path = NormalizeDiffPath(parts[2]);
                var status = added > 0 && removed > 0 ? "修改"
                    : added > 0 ? "新建"
                    : "删除";
                entries.Add(new ReviewFileEntry(path, added, removed, status));
            }
        }

        return entries;
    }

    /// <summary>
    /// Normalize a path from git numstat/name-status output into a pathspec that
    /// <c>git diff -- &lt;path&gt;</c> accepts: strip quotes and resolve
    /// <c>{old =&gt; new}</c> rename syntax to the new path.
    /// </summary>
    internal static string NormalizeDiffPath(string path)
    {
        var trimmed = path.Trim().Trim('"');
        return ResolveRenamePath(trimmed);
    }

    /// <summary>
    /// Resolve git's <c>{old => new}</c> rename syntax to the actual new path.
    /// Examples:
    /// <list type="bullet">
    ///   <item><c>src/{OneCode.Infrastructure => OneCode.Automation}/Cron/File.cs</c> → <c>src/OneCode.Automation/Cron/File.cs</c></item>
    ///   <item><c>{old_name => new_name}.cs</c> → <c>new_name.cs</c></item>
    ///   <item><c>src/file.{old => new}</c> → <c>src/file.new</c></item>
    /// </list>
    /// Paths without rename braces are returned unchanged.
    /// </summary>
    internal static string ResolveRenamePath(string path)
    {
        var result = path;
        while (true)
        {
            var start = result.IndexOf('{');
            if (start < 0) break;
            var arrow = result.IndexOf("=>", start, StringComparison.Ordinal);
            if (arrow < 0) break;
            var end = result.IndexOf('}', arrow);
            if (end < 0) break;

            // Extract the new path segment (after "=>" and before "}").
            var newSegment = result[(arrow + 2)..end].Trim();
            result = result[..start] + newSegment + result[(end + 1)..];
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<string> GetFileDiffAgainstHeadAsync(
        string filePath,
        CancellationToken ct = default,
        string? workingDirectory = null)
    {
        // Defensive: callers may still pass raw numstat rename syntax.
        var path = NormalizeDiffPath(filePath);
        if (string.IsNullOrWhiteSpace(path))
            return "";

        // Critical: --numstat paths are repo-root relative, but `git diff -- <path>`
        // pathspecs are cwd-relative when the process is inside a subdirectory
        // (e.g. `dotnet run` from src/OneCode.Cli). Always execute from repo root,
        // and prefix with :(top) so pathspecs stay root-anchored even if cwd drifts.
        var root = await ResolveGitRootAsync(workingDirectory, ct).ConfigureAwait(false);
        var topPath = ToTopLevelPathspec(path);

        // Prefer combined staged+unstaged vs HEAD; fall back to cached / unstaged
        // when HEAD diff is empty (rare pathspec / index edge cases).
        string[][] attempts =
        [
            ["-c", "core.quotepath=false", "diff", "--no-ext-diff", "--no-color", "HEAD", "--", topPath],
            ["-c", "core.quotepath=false", "diff", "--no-ext-diff", "--no-color", "--cached", "--", topPath],
            ["-c", "core.quotepath=false", "diff", "--no-ext-diff", "--no-color", "--", topPath],
        ];

        foreach (var args in attempts)
        {
            var result = await RunAsync(args, root, ct).ConfigureAwait(false);
            if (result is null)
            {
                _logger?.LogWarning("GetFileDiffAgainstHeadAsync: git returned null for {Path} (root={Root})", path, root);
                return "";
            }

            if (!string.IsNullOrWhiteSpace(result.Stdout))
                return result.Stdout;

            if (!result.Success && !string.IsNullOrWhiteSpace(result.Stderr))
            {
                _logger?.LogDebug(
                    "GetFileDiffAgainstHeadAsync: empty stdout for {Path}, stderr={Stderr}",
                    path,
                    result.Stderr.Trim());
            }
        }

        return "";
    }

    /// <summary>
    /// Resolve the git working tree root for <paramref name="workingDirectory"/>
    /// (or the process cwd). Falls back to the input directory when not in a repo.
    /// </summary>
    private async Task<string> ResolveGitRootAsync(string? workingDirectory, CancellationToken ct)
    {
        var cwd = workingDirectory ?? Directory.GetCurrentDirectory();
        var root = await GetRepositoryRootAsync(cwd, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(root) ? cwd : root;
    }

    /// <summary>
    /// Anchor a repo-root-relative path with git's <c>:(top)</c> pathspec magic so
    /// <c>git diff -- &lt;path&gt;</c> works regardless of the process cwd.
    /// </summary>
    internal static string ToTopLevelPathspec(string path)
    {
        var normalized = path.Replace('\\', '/');
        const string top = ":(top)";
        if (normalized.StartsWith(top, StringComparison.Ordinal))
            return normalized;
        // Absolute paths must not get :(top) — git treats them as filesystem paths.
        if (Path.IsPathRooted(path))
            return path;
        return top + normalized;
    }

    /// <summary>
    /// Format the success-path output of a git command. Returns the trimmed
    /// stdout, or "(empty)" when stdout is blank/whitespace.
    /// </summary>
    public static string FormatOutput(GitCommandResult result)
    {
        return string.IsNullOrWhiteSpace(result.Stdout)
            ? "(empty)"
            : result.Stdout.Trim();
    }

    /// <summary>
    /// Format the failure-path output of a git command.
    /// </summary>
    public static string FormatFailure(GitCommandResult result)
    {
        return string.IsNullOrWhiteSpace(result.Stderr)
            ? FormatOutput(result)
            : result.Stderr.Trim();
    }
}
