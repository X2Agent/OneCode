using System.ComponentModel;
using OneCode.Infrastructure;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace OneCode.App.Tools;

/// <summary>
/// Glob pattern file finder using Microsoft.Extensions.FileSystemGlobbing.
/// Supports full glob patterns including **, *.ts, src/**/*.cs, etc.
///
/// Uses <see cref="FileIgnore"/> to exclude build outputs, caches, VCS internals, and
/// other noise directories by default.
/// </summary>
public sealed class GlobTool
{
    private readonly IWorkingDirectoryAccessor _wd;

    public GlobTool(IWorkingDirectoryAccessor wd) => _wd = wd;

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
            return ToolResult.Error(resolveResult.Error);
        var fullPath = resolveResult.Value;

        if (!Directory.Exists(fullPath))
            return ToolResult.Error($"Directory not found: {path}");

        try
        {
            var files = await Task.Run(() => FindFiles(fullPath, pattern), ct);

            if (files.Count == 0)
                return ToolResult.Success($"No files matching '{pattern}' in '{path}'");

            var header = files.Count == 1
                ? $"Found 1 file matching '{pattern}':"
                : $"Found {files.Count} files matching '{pattern}':";

            return ToolResult.Success($"{header}\n{string.Join("\n", files)}");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Error searching for files: {ex.Message}");
        }
    }

    private static List<string> FindFiles(string baseDir, string pattern)
    {
        pattern = pattern.Replace('\\', '/');

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(pattern);

        FileIgnore.ApplyExcludes(matcher);

        var dirInfo = new DirectoryInfoWrapper(new DirectoryInfo(baseDir));
        var result = matcher.Execute(dirInfo);

        return result.Files
            .Select(f => f.Path.Replace('/', Path.DirectorySeparatorChar))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
