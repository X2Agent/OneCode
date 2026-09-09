using System.ComponentModel;
using OneCode.App.Services.Search;
using OneCode.Infrastructure;

namespace OneCode.App.Tools;

/// <summary>
/// Search file contents with regex using ripgrep (or native C# fallback).
/// 搜索内核委托给 <see cref="ITextSearchService"/>，本类负责路径安全校验、分页与结果格式化。
/// </summary>
public sealed class GrepTool
{
    private const int DefaultHeadLimit = 250;

    private readonly ITextSearchService _textSearch;
    private readonly IWorkingDirectoryAccessor _wd;

    public GrepTool(ITextSearchService textSearch, IWorkingDirectoryAccessor wd)
        => (_textSearch, _wd) = (textSearch, wd);

    [Description("Search file contents by regex, returning matching files, lines, or counts. " +
                 "Engine: uses ripgrep (rg) when available for speed and native regex semantics; falls back to a C# Regex-based scanner otherwise. " +
                 "Default excludes: .git, .svn, node_modules, bin, obj — automatically filtered. " +
                 "Output modes: 'files_with_matches' (default, just file paths), 'content' (matching lines with line numbers), 'count' (per-file match count). " +
                 "Pagination: use head_limit + offset to page through large result sets (default 250 entries per call). " +
                 "Context: A (after), B (before), C (symmetric) — only honored in 'content' mode. " +
                 "Multiline: set multiline=true to match patterns that span newlines (e.g. 'class \\s+\\{[\\s\\S]*?\\}'). " +
                 "Path safety: must resolve within the working directory.")]
    public async Task<ToolResult> SearchAsync(
        [Description("Regular expression pattern to search for. Uses ripgrep/PCRE syntax when rg is available, otherwise .NET Regex. " +
                     "Examples: 'function\\s+\\w+', 'TODO|FIXME', 'class\\s+\\w+\\s*\\{'. Use -e prefix by adding a leading dash if the pattern starts with one.")] string pattern,
        [Description("File or directory to search in. Default: current working directory. Must resolve within the working directory.")] string? path = null,
        [Description("Glob pattern(s) to include, comma-separated. Examples: '*.cs', '*.cs,*.tsx'. Filters which files are scanned.")] string? glob = null,
        [Description("Glob pattern(s) to exclude, comma-separated. Examples: '*.Tests.cs', '**/bin/**'. Applied after include filters.")] string? exclude_glob = null,
        [Description("Output mode: 'files_with_matches' (default, file paths only), 'content' (matching lines with line numbers and optional context), 'count' (per-file match count).")] string? output_mode = "files_with_matches",
        [Description("Case-insensitive search. Default false.")] bool i = false,
        [Description("Enable cross-line matching so patterns can span newlines. Default false. Uses .NET Singleline mode in native fallback.")] bool multiline = false,
        [Description("Lines of context to show AFTER each match. Only honored in 'content' mode. Ignored if C is set.")] int? A = null,
        [Description("Lines of context to show BEFORE each match. Only honored in 'content' mode. Ignored if C is set.")] int? B = null,
        [Description("Symmetric context: lines of context before AND after each match. Overrides A and B when set. Only honored in 'content' mode.")] int? C = null,
        [Description("Limit output to the first N entries (default 250). Use with offset to paginate. Set to 0 for unlimited.")] int head_limit = DefaultHeadLimit,
        [Description("Skip the first N entries before applying head_limit. Use for pagination.")] int offset = 0,
        CancellationToken ct = default)
    {
        var om = output_mode ?? "files_with_matches";
        var contextSymmetric = C;

        var workingDir = _wd.WorkingDirectory;
        var resolveResult = PathsHelper.SafeResolve(path ?? ".", workingDir, _wd.AdditionalDirectories);
        if (!resolveResult.IsSuccess)
            return ToolResult.Error(resolveResult.Error ?? "Path resolution failed");
        var searchPath = resolveResult.Value;

        if (!Directory.Exists(searchPath) && !File.Exists(searchPath))
            return ToolResult.Error($"Path does not exist: {path}");

        List<string> results;
        try
        {
            var request = new TextSearchRequest(
                SearchPath: searchPath,
                Pattern: pattern,
                Glob: glob,
                ExcludeGlob: exclude_glob,
                CaseInsensitive: i,
                Multiline: multiline,
                OutputMode: om,
                ContextBefore: contextSymmetric ?? (B ?? 0),
                ContextAfter: contextSymmetric ?? (A ?? 0),
                WorkspaceRoot: workingDir);

            results = [.. await _textSearch.SearchAsync(request, ct).ConfigureAwait(false)];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Search failed: {ex.Message}");
        }

        var (limitedResults, appliedLimit, totalCount) = ApplyHeadLimit(results, head_limit, offset);
        return ToolResult.Success(FormatOutput(limitedResults, om, appliedLimit, totalCount, searchPath));
    }

    private static (List<string> Items, int? AppliedLimit, int TotalCount) ApplyHeadLimit(List<string> items, int headLimit, int offset)
    {
        var total = items.Count;
        if (headLimit <= 0) return (items.Skip(offset).ToList(), null, total);
        var wasTruncated = (total - offset) > headLimit;
        var limited = items.Skip(offset).Take(headLimit).ToList();
        return (limited, wasTruncated ? headLimit : (int?)null, total);
    }

    private static string FormatOutput(List<string> items, string outputMode, int? appliedLimit, int totalCount, string searchPath)
    {
        var truncNote = appliedLimit.HasValue ? $" (showing {appliedLimit} of {totalCount} total, use offset to page)" : "";
        if (outputMode == "files_with_matches")
        {
            if (items.Count == 0) return "No files found";
            return $"Found {items.Count} files{truncNote}:\n" + string.Join("\n", items);
        }
        if (outputMode == "count")
        {
            var totalMatches = items.Sum(line =>
            {
                var colonIdx = line.LastIndexOf(':');
                return colonIdx > 0 && int.TryParse(line.AsSpan(colonIdx + 1), out var count) ? count : 0;
            });
            return $"Found {totalMatches} matches across {items.Count} files{truncNote}\n" + string.Join("\n", items);
        }
        if (items.Count == 0) return "No matches found";
        return string.Join("\n", items) + (appliedLimit.HasValue ? $"\n[Truncated: showing {appliedLimit} of {totalCount} results. Use offset to page.]" : "");
    }
}
