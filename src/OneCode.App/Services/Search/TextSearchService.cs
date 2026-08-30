using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using OneCode.Core.IO;

namespace OneCode.App.Services.Search;

/// <summary>
/// 文本搜索内核实现。ripgrep 可用时优先（速度 + 原生 PCRE 语义），否则回退到 C# Regex 逐文件扫描。
/// </summary>
public sealed class TextSearchService(
    IProcessRunner processRunner,
    IFileSystem fileSystem,
    ILogger<TextSearchService> logger) : ITextSearchService
{
    public async Task<IReadOnlyList<string>> SearchAsync(TextSearchRequest request, CancellationToken ct = default)
    {
        var contextBefore = request.ContextBefore;
        var contextAfter = request.ContextAfter;
        // Context 仅对 content 模式生效。
        if (request.OutputMode != "content")
        {
            contextBefore = 0;
            contextAfter = 0;
        }

        return await processRunner.CommandExistsAsync("rg").ConfigureAwait(false)
            ? await SearchRipgrepAsync(request, contextBefore, contextAfter, ct).ConfigureAwait(false)
            : await SearchNativeAsync(request, contextBefore, contextAfter, ct).ConfigureAwait(false);
    }

    private async Task<List<string>> SearchRipgrepAsync(
        TextSearchRequest request, int contextBefore, int contextAfter, CancellationToken ct)
    {
        var args = new List<string> { "--hidden", "--glob", "!.git", "--glob", "!.svn", "--glob", "!node_modules", "--max-columns", "500" };
        if (request.CaseInsensitive) args.Add("-i");
        if (request.Multiline) args.Add("--multiline");
        switch (request.OutputMode)
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
        if (request.Pattern.StartsWith("-", StringComparison.Ordinal)) { args.Add("-e"); }
        args.Add(request.Pattern);

        if (!string.IsNullOrEmpty(request.Glob))
        {
            foreach (var g in request.Glob.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            { args.Add("--glob"); args.Add(g); }
        }
        if (!string.IsNullOrEmpty(request.ExcludeGlob))
        {
            foreach (var eg in request.ExcludeGlob.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            { args.Add("--glob"); args.Add($"!{eg}"); }
        }

        var result = await processRunner.ExecuteAsync("rg", args.ToArray(), request.SearchPath, ct: ct);
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
        var prefix = request.SearchPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return lines.Select(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? l[prefix.Length..] : l).ToList();
    }

    private async Task<List<string>> SearchNativeAsync(
        TextSearchRequest request, int contextBefore, int contextAfter, CancellationToken ct)
    {
        List<string> results = [];
        var regexOptions = request.CaseInsensitive ? RegexOptions.IgnoreCase | RegexOptions.Compiled : RegexOptions.Compiled;
        if (request.Multiline) regexOptions |= RegexOptions.Singleline;
        Regex regex;
        try { regex = new Regex(request.Pattern, regexOptions); }
        catch (ArgumentException ex) { return new List<string> { $"Invalid regex: {ex.Message}" }; }

        var excludePatterns = string.IsNullOrEmpty(request.ExcludeGlob)
            ? []
            : request.ExcludeGlob.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var defaultExcludes = new[] { ".git", ".svn", "node_modules", "bin", "obj" };
        var files = fileSystem.FindFiles(request.SearchPath, request.Glob, defaultExcludes);

        if (excludePatterns.Length > 0)
        {
            files = files.Where(f =>
            {
                var rel = GetRelativePath(f, request.SearchPath).Replace('\\', '/');
                return !excludePatterns.Any(ep => IsGlobMatch(rel, ep));
            }).ToList();
        }

        foreach (var file in files)
        {
            try
            {
                var relativePath = GetRelativePath(file, request.SearchPath);
                if (request.Multiline)
                {
                    results.AddRange(await SearchNativeMultilineAsync(file, relativePath, regex, request.OutputMode, ct).ConfigureAwait(false));
                    continue;
                }
                if (request.OutputMode == "files_with_matches")
                {
                    var found = false;
                    await foreach (var line in File.ReadLinesAsync(file, ct).ConfigureAwait(false))
                    {
                        if (regex.IsMatch(line)) { found = true; break; }
                    }
                    if (found) results.Add(relativePath);
                    continue;
                }
                if (request.OutputMode == "count")
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
                logger.LogDebug(ex, "TextSearchService: skipping unreadable file {File}", file);
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
            logger.LogDebug(ex, "TextSearchService.SearchNativeMultiline: unreadable {File}", file);
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
            logger.LogDebug(ex, "TextSearchService.GetRelativePath failed for {FullPath} under {SearchPath}", fullPath, searchPath);
            return fullPath;
        }
    }

    private static bool IsGlobMatch(string relativePath, string pattern)
    {
        var matcher = new Matcher();
        matcher.AddInclude(pattern.Replace('\\', '/'));
        return matcher.Match(relativePath.Replace('\\', '/')).HasMatches;
    }
}
