using System.ComponentModel;
using System.Text.RegularExpressions;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Abstractions;
using Microsoft.Extensions.FileSystemGlobbing;

namespace OneCode.App.Tools;

/// <summary>
/// Search file contents with regex using ripgrep (or native C# fallback).
/// </summary>
public sealed class GrepTool
{
    private const int DefaultHeadLimit = 250;

    private readonly IProcessRunner _processRunner;
    private readonly IFileSystem _fileSystem;
    private readonly IWorkingDirectoryAccessor _wd;
    private readonly ILogger<GrepTool> _logger;

    public GrepTool(IProcessRunner processRunner, IFileSystem fileSystem, IWorkingDirectoryAccessor wd, ILogger<GrepTool>? logger = null)
        => (_processRunner, _fileSystem, _wd, _logger) = (processRunner, fileSystem, wd,
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GrepTool>.Instance);

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
        var caseInsensitive = i;
        var om = output_mode ?? "files_with_matches";

        var contextSymmetric = C;
        var contextBefore = contextSymmetric ?? (B ?? 0);
        var contextAfter = contextSymmetric ?? (A ?? 0);
        if (om != "content") { contextBefore = 0; contextAfter = 0; }

        var workingDir = _wd.WorkingDirectory;
        var resolveResult = PathsHelper.SafeResolve(path ?? ".", workingDir, _wd.AdditionalDirectories);
        if (!resolveResult.IsSuccess)
            return ToolResult.Error(resolveResult.Error);
        var searchPath = resolveResult.Value;

        if (!Directory.Exists(searchPath) && !File.Exists(searchPath))
            return ToolResult.Error($"Path does not exist: {path}");

        List<string> results;
        try
        {
            results = await SearchAsync(searchPath, pattern, glob, exclude_glob, caseInsensitive, multiline,
                om, contextBefore, contextAfter, ct);
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

    private async Task<List<string>> SearchAsync(
        string searchPath, string pattern, string? glob, string? excludeGlob, bool caseInsensitive,
        bool multiline, string outputMode, int contextBefore, int contextAfter,
        CancellationToken ct)
    {
        if (await _processRunner.CommandExistsAsync("rg").ConfigureAwait(false))
            return await SearchRipgrepAsync(searchPath, pattern, glob, excludeGlob, caseInsensitive, multiline,
                outputMode, contextBefore, contextAfter, ct);
        return await SearchNativeAsync(searchPath, pattern, glob, excludeGlob, caseInsensitive, multiline,
            outputMode, contextBefore, contextAfter, ct);
    }

    private async Task<List<string>> SearchRipgrepAsync(
        string searchPath, string pattern, string? glob, string? excludeGlob, bool caseInsensitive,
        bool multiline, string outputMode, int contextBefore, int contextAfter,
        CancellationToken ct)
    {
        var args = new List<string> { "--hidden", "--glob", "!.git", "--glob", "!.svn", "--glob", "!node_modules", "--max-columns", "500" };
        if (caseInsensitive) args.Add("-i");
        if (multiline) args.Add("--multiline");
        switch (outputMode)
        {
            case "files_with_matches": args.Add("-l"); break;
            case "count": args.Add("-c"); break;
            default:
                if (contextBefore > 0 && contextAfter > 0 && contextBefore == contextAfter)
                { args.Add("--context"); args.Add(contextBefore.ToString(CultureInfo.InvariantCulture)); }
                else
                {
                    if (contextBefore > 0) { args.Add("--before-context"); args.Add(contextBefore.ToString(CultureInfo.InvariantCulture)); }
                    if (contextAfter > 0) { args.Add("--after-context"); args.Add(contextAfter.ToString(CultureInfo.InvariantCulture)); }
                }
                break;
        }
        if (pattern.StartsWith("-", StringComparison.Ordinal)) { args.Add("-e"); }
        args.Add(pattern);

        if (!string.IsNullOrEmpty(glob))
        {
            foreach (var g in glob.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            { args.Add("--glob"); args.Add(g); }
        }
        if (!string.IsNullOrEmpty(excludeGlob))
        {
            foreach (var eg in excludeGlob.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            { args.Add("--glob"); args.Add($"!{eg}"); }
        }

        var result = await _processRunner.ExecuteAsync("rg", args.ToArray(), searchPath, ct: ct);
        if (result == null)
            return [];

        // ripgrep exit code 1 = no matches (not an error), exit code 2+ = actual error
        if (!result.Success)
        {
            var errMsg = result.Stderr?.Trim();
            if (!string.IsNullOrEmpty(errMsg))
                throw new InvalidOperationException($"ripgrep error: {errMsg}");
            return [];
        }

        var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var prefix = searchPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return lines.Select(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? l[prefix.Length..] : l).ToList();
    }

    private async Task<List<string>> SearchNativeAsync(
        string searchPath, string pattern, string? glob, string? excludeGlob, bool caseInsensitive,
        bool multiline, string outputMode, int contextBefore, int contextAfter,
        CancellationToken ct)
    {
        List<string> results = [];
        var regexOptions = caseInsensitive ? RegexOptions.IgnoreCase | RegexOptions.Compiled : RegexOptions.Compiled;
        if (multiline) regexOptions |= RegexOptions.Singleline;
        Regex regex;
        try { regex = new Regex(pattern, regexOptions); }
        catch (ArgumentException ex) { return new List<string> { $"Invalid regex: {ex.Message}" }; }

        var excludePatterns = string.IsNullOrEmpty(excludeGlob)
            ? []
            : excludeGlob.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var searchPattern = string.IsNullOrEmpty(glob) ? null : glob;
        var defaultExcludes = new[] { ".git", ".svn", "node_modules", "bin", "obj" };
        var files = _fileSystem.FindFiles(searchPath, searchPattern, defaultExcludes);

        if (excludePatterns.Length > 0)
        {
            files = files.Where(f =>
            {
                var rel = GetRelativePath(f, searchPath).Replace('\\', '/');
                return !excludePatterns.Any(ep => IsGlobMatch(rel, ep));
            }).ToList();
        }

        foreach (var file in files)
        {
            try
            {
                var relativePath = GetRelativePath(file, searchPath);
                if (multiline)
                {
                    results.AddRange(await SearchNativeMultilineAsync(file, relativePath, regex, outputMode, ct).ConfigureAwait(false));
                    continue;
                }
                if (outputMode == "files_with_matches")
                {
                    var found = false;
                    await foreach (var line in File.ReadLinesAsync(file, ct).ConfigureAwait(false))
                    {
                        if (regex.IsMatch(line)) { found = true; break; }
                    }
                    if (found) results.Add(relativePath);
                    continue;
                }
                if (outputMode == "count")
                {
                    var count = 0;
                    await foreach (var line in File.ReadLinesAsync(file, ct).ConfigureAwait(false))
                        count += regex.Matches(line).Count;
                    if (count > 0) results.Add($"{relativePath}:{count}");
                    continue;
                }
                if (contextBefore == 0 && contextAfter == 0)
                {
                    var lineIndex = 0;
                    await foreach (var line in File.ReadLinesAsync(file, ct).ConfigureAwait(false))
                    {
                        lineIndex++;
                        if (regex.IsMatch(line)) results.Add($"{relativePath}:{lineIndex}:{line}");
                    }
                }
                else
                {
                    results.AddRange(await SearchNativeWithContextAsync(file, relativePath, regex, contextBefore, contextAfter, ct).ConfigureAwait(false));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GrepTool: skipping unreadable file {File}", file);
            }
        }
        return results;
    }

    private async Task<List<string>> SearchNativeMultilineAsync(
        string file, string relativePath, Regex regex, string outputMode, CancellationToken ct)
    {
        string content;
        try { content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GrepTool.SearchNativeMultiline: unreadable {File}", file);
            return [];
        }

        var results = new List<string>();
        var matches = regex.Matches(content);
        if (matches.Count == 0) return results;

        if (outputMode == "files_with_matches") { results.Add(relativePath); return results; }
        if (outputMode == "count") { results.Add($"{relativePath}:{matches.Count}"); return results; }

        foreach (Match m in matches)
        {
            var lineNum = 1;
            for (var i = 0; i < m.Index && i < content.Length; i++)
                if (content[i] == '\n') lineNum++;

            var matchText = m.Value;
            var newlineIdx = matchText.IndexOf('\n');
            var snippet = newlineIdx >= 0 ? matchText[..newlineIdx] + "..." : matchText;
            results.Add($"{relativePath}:{lineNum}:{snippet}");
        }
        return results;
    }

    private static async Task<List<string>> SearchNativeWithContextAsync(
        string file, string relativePath, Regex regex, int contextBefore, int contextAfter,
        CancellationToken ct)
    {
        var allLines = await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false);
        var matchLineNums = new HashSet<int>();

        for (var i = 0; i < allLines.Length; i++)
        {
            if (regex.IsMatch(allLines[i]))
                matchLineNums.Add(i);
        }

        var results = new List<string>();
        if (matchLineNums.Count == 0) return results;

        var ranges = BuildContextRanges(matchLineNums, contextBefore, contextAfter, allLines.Length);

        var firstGroup = true;
        foreach (var (start, end) in ranges)
        {
            if (!firstGroup) results.Add("--");
            firstGroup = false;

            for (var i = start; i <= end; i++)
            {
                var lineNum = i + 1;
                var sep = matchLineNums.Contains(i) ? ":" : "-";
                results.Add($"{relativePath}{sep}{lineNum}{sep}{allLines[i]}");
            }
        }
        return results;
    }

    private static List<(int Start, int End)> BuildContextRanges(
        HashSet<int> matchLines, int before, int after, int totalLines)
    {
        var ranges = matchLines
            .Select(m => (Start: Math.Max(0, m - before), End: Math.Min(totalLines - 1, m + after)))
            .OrderBy(r => r.Start)
            .ToList();

        List<(int Start, int End)> merged = [];
        foreach (var (s, e) in ranges)
        {
            if (merged.Count > 0 && s <= merged[^1].End + 1)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, e));
            else
                merged.Add((s, e));
        }
        return merged;
    }

    private string GetRelativePath(string fullPath, string searchPath)
    {
        try { return Path.GetRelativePath(searchPath, fullPath); }
        catch (Exception ex)
        {
            if (_logger is not null)
                _logger.LogDebug(ex, "GrepTool.GetRelativePath failed for {FullPath} under {SearchPath}", fullPath, searchPath);
            else
                System.Diagnostics.Debug.WriteLine($"GrepTool.GetRelativePath failed for {fullPath} under {searchPath}: {ex.Message}");
            return fullPath;
        }
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

    private static bool IsGlobMatch(string relativePath, string pattern)
    {
        var matcher = new Matcher();
        matcher.AddInclude(pattern.Replace('\\', '/'));
        return matcher.Match(relativePath.Replace('\\', '/')).HasMatches;
    }
}
