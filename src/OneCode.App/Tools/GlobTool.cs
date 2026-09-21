using System.ComponentModel;
using OneCode.Infrastructure;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace OneCode.App.Tools;

/// <summary>
/// Glob pattern file finder using Microsoft.Extensions.FileSystemGlobbing.
/// Supports full glob patterns including **, *.ts, src/**/*.cs, etc.
///
/// Uses the workspace ignore snapshot (<see cref="WorkspaceIgnoreSnapshot"/>) to exclude
/// build outputs, caches, VCS internals, other noise directories, and user-declared
/// `.onecodeignore` paths by default.
/// </summary>
public sealed class GlobTool
{
    private readonly IWorkingDirectoryAccessor _wd;
    private readonly IWorkspaceIgnoreProvider? _ignoreProvider;

    public GlobTool(IWorkingDirectoryAccessor wd, IWorkspaceIgnoreProvider? ignoreProvider = null)
        => (_wd, _ignoreProvider) = (wd, ignoreProvider);

    [Description("Find files by glob pattern, returning a sorted list of matching paths. " +
                 "Use this to discover files by name or extension when you do not need to inspect content (use Grep for content search). " +
                 "Patterns support ** (recursive), * (single segment), and ? (single char). Examples: 'src/**/*.cs', '*.ts', '**/package.json'. " +
                 "Default excludes: build outputs (bin/obj), VCS internals (.git/.svn), caches (node_modules) — these are filtered out by FileIgnore. " +
                 "Path safety: must resolve within the working directory. " +
                 "Returns 'No files matching' when no files are found; check spelling and pattern syntax in that case.")]
    public async Task<ToolResult> GlobAsync(
        [Description("Glob pattern to match. Use ** for recursive, * for a single path segment, ? for a single character. " +
                     "Examples: 'src/**/*.cs', '*.ts', '**/package.json'. Backslashes are normalized to forward slashes.")] string pattern,
        [Description("Directory to search in. Default: current working directory. Must resolve within the working directory.")] string? path = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return ToolResult.Error("Pattern is required");

        var workingDir = _wd.WorkingDirectory;
        var resolveResult = PathsHelper.SafeResolve(path ?? ".", workingDir, _wd.AdditionalDirectories);
        if (!resolveResult.IsSuccess)
            return ToolResult.Error(resolveResult.Error ?? "Path resolution failed");
        var fullPath = resolveResult.Value;

        if (!Directory.Exists(fullPath))
            return ToolResult.Error($"Directory not found: {path}");

        try
        {
            var ignore = _ignoreProvider is null
                ? new WorkspaceIgnoreSnapshot(null, [], [])
                : await _ignoreProvider.GetSnapshotAsync(ct).ConfigureAwait(false);
            var workspaceRoot = Path.GetFullPath(_wd.WorkingDirectory);

            // Task.Run's token only prevents the task from *starting*; once the synchronous
            // enumeration is running it cannot be interrupted. Checking inside the loop makes a
            // cancel take effect during a large tree instead of after it finishes.
            var files = await Task.Run(() => FindFiles(fullPath, pattern, ignore, workspaceRoot, ct), ct)
                .ConfigureAwait(false);

            if (files.Count == 0)
                return ToolResult.Success($"No files matching '{pattern}' in '{path}'");

            var header = files.Count == 1
                ? $"Found 1 file matching '{pattern}':"
                : $"Found {files.Count} files matching '{pattern}':";

            return ToolResult.Success($"{header}\n{string.Join("\n", files)}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Error searching for files: {ex.Message}");
        }
    }

    private static List<string> FindFiles(
        string baseDir, string pattern, WorkspaceIgnoreSnapshot ignore, string workspaceRoot, CancellationToken ct)
    {
        pattern = pattern.Replace('\\', '/');

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(pattern);

        ignore.ApplyBuiltInExcludes(matcher);

        var dirInfo = new DirectoryInfoWrapper(new DirectoryInfo(baseDir));
        var result = matcher.Execute(dirInfo);

        List<string> files = [];
        foreach (var file in result.Files)
        {
            ct.ThrowIfCancellationRequested();

            var path = file.Path.Replace('/', Path.DirectorySeparatorChar);
            if (ignore.IsIgnored(ToWorkspaceRelative(Path.Combine(baseDir, path), workspaceRoot)))
                continue;

            files.Add(path);
        }

        return files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Converts a matched file path to workspace-relative before ignore evaluation.
    /// <see cref="Matcher.Execute"/> 返回相对搜索根（即 <see cref="FindFiles"/> 的 baseDir）的路径，
    /// 而用户规则的 `/` 锚定与 `**/` 语义均以工作区根为基准，因此必须先与搜索根组合为绝对路径、
    /// 再换算为工作区相对，才能交给 <see cref="WorkspaceIgnoreSnapshot.IsIgnored"/> 判定；
    /// 否则从子目录搜索时根锚定规则（如 <c>/generated/</c>）会错误命中子目录下的同名路径。
    /// </summary>
    private static string ToWorkspaceRelative(string fullPath, string workspaceRoot)
    {
        if (string.IsNullOrEmpty(workspaceRoot)) return fullPath;
        try { return Path.GetRelativePath(workspaceRoot, fullPath); }
        catch (Exception) { return fullPath; }
    }
}
