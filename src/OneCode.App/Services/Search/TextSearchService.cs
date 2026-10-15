using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using OneCode.App.Tools;
using OneCode.Core.IO;
using OneCode.Infrastructure;

namespace OneCode.App.Services.Search;

/// <summary>
/// 文本搜索内核实现。ripgrep 可用时优先（速度 + 原生 PCRE 语义），否则回退到 C# Regex 逐文件扫描。
/// </summary>
public sealed class TextSearchService(
    IProcessRunner processRunner,
    LocalAgentFileStore fileSystem,
    ILogger<TextSearchService> logger,
    IWorkspaceIgnoreProvider? ignoreProvider = null) : ITextSearchService
{
    /// <summary>
    /// 底层文件枚举默认排除目录（复用 <see cref="FileIgnore.Folders"/>），
    /// 涵盖 .NET、Node、Rust、Python、Java、Go 等多语言构建与缓存产物。
    /// </summary>
    private static readonly string[] DefaultExcludes = [.. FileIgnore.Folders];

    /// <summary>
    /// Upper bound for a single native regex evaluation.
    /// </summary>
    /// <remarks>
    /// Without a timeout a pathological pattern (nested quantifiers over a long line) can run
    /// unbounded inside one file, and the tool call never returns. 5s matches the framework's own
    /// default for the file-skills regexes.
    /// </remarks>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 单行最大字符列宽（与 ripgrep <c>--max-columns 500</c> 对齐）。
    /// 正常源码行通常 &lt;200 字符（100% 零信息损失），单行巨型文件（如数 MB 的 <c>.jsonl</c> 或 <c>.min.js</c>）
    /// 超出该列宽时截取匹配窗口并附截断提示，防止单次 Grep 吐出数十 MB 撑爆上下文。
    /// </summary>
    public const int MaxLineColumns = 500;
    private const string LineTruncatedSuffix = " ... [line truncated, use Read with offset/limit]";
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

        var ignore = ignoreProvider is null
            ? new WorkspaceIgnoreSnapshot(null, [], [])
            : await ignoreProvider.GetSnapshotAsync(ct).ConfigureAwait(false);
        return await processRunner.CommandExistsAsync("rg").ConfigureAwait(false)
            ? await SearchRipgrepAsync(request, ignore, contextBefore, contextAfter, ct).ConfigureAwait(false)
            : await SearchNativeAsync(request, ignore, contextBefore, contextAfter, ct).ConfigureAwait(false);
    }

    private async Task<List<string>> SearchRipgrepAsync(
        TextSearchRequest request, WorkspaceIgnoreSnapshot ignore,
        int contextBefore, int contextAfter, CancellationToken ct)
    {
        var workspaceRoot = request.WorkspaceRoot ?? request.SearchPath;
        var isWithinWorkspace = PathBoundary.IsWithinDirectory(request.SearchPath, workspaceRoot);
        var effectiveRoot = isWithinWorkspace
            ? workspaceRoot
            : (File.Exists(request.SearchPath)
                ? (Path.GetDirectoryName(request.SearchPath) ?? request.SearchPath)
                : request.SearchPath);
        var args = new List<string>
        {
            "--hidden",
            "--no-ignore",                    // 关闭 rg 原生 .gitignore/.ignore/.rgignore 读取，避免第三套语义
            "--ignore-file-case-insensitive", // 与 C# fallback 的 OrdinalIgnoreCase 匹配对齐
            "--glob-case-insensitive",        // 与 FileIgnore 的大小写不敏感匹配对齐
            "--max-columns", "500",
            "--max-columns-preview",
        };

        // 内置排除规则由 OneCode 统一生成 rg glob 参数（与 FileIgnore / C# fallback 同一份清单），
        // 不依赖 rg 自身的 ignore 文件解析——两条搜索路径对同一规则必须给出相同结果。
        foreach (var dir in FileIgnore.Folders)
        {
            // 三个互补形态，与 fallback 判定（FileIgnore.IsIgnored 的段匹配 + FilePatterns glob）对齐：
            // ① 任意层级下该目录的**内容**；② 根级目录的内容；③ 目录条目本身——
            // 段匹配还会把"与目录同名的文件"排除（如 `ls out` 命中文件 `out`），rg 路径同样需要。
            args.Add("--glob"); args.Add($"!{dir}/**");
            args.Add("--glob"); args.Add($"!**/{dir}/**");
            args.Add("--glob"); args.Add($"!{dir}");
        }
        foreach (var pattern in FileIgnore.FilePatterns)
        { args.Add("--glob"); args.Add($"!{pattern}"); }

        if (ignore.Path is not null)
        {
            // rg 以工作区根为运行目录，`--ignore-file` 的 `/foo` 根锚定与 `**/` 语义
            // 与 fallback 判定器（工作区根相对路径）对齐。
            args.Add("--ignore-file");
            args.Add(ignore.Path);
        }
        if (request.CaseInsensitive) args.Add("-i");
        if (request.Multiline) args.Add("--multiline");
        switch (request.OutputMode)
        {
            case "files_with_matches": args.Add("-l"); break;
            case "count": args.Add("-c"); break;
            default:
                // content 模式：必须强制输出文件名和行号（重定向 stdout 与单文件搜索场景默认不输出）
                args.Add("-n");
                args.Add("-H");
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

        // 当搜索目标位于工作区外的附加目录（如 ~/.onecode/events）时，以该目录为 rg 工作目录并传相对路径，
        // 避免绝对路径中的祖先目录名（如 ~/.onecode）误触发 `--glob !**/.onecode/**`。
        var rgTarget = isWithinWorkspace
            ? request.SearchPath
            : (File.Exists(request.SearchPath) ? Path.GetFileName(request.SearchPath) : ".");
        args.Add(rgTarget);

        var result = await processRunner.ExecuteAsync("rg", args.ToArray(), effectiveRoot, ct: ct);
        if (result is null)
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
        return lines.Select(line => NormalizeRgPath(line, request.SearchPath, effectiveRoot)).ToList();
    }

    /// <summary>
    /// rg 输出路径以进程运行目录（工作区根）为基准；换算为相对 <paramref name="searchPath"/>，
    /// 使 ripgrep 路径的显示结果与 C# fallback（<see cref="GetRelativePath"/> 相对 searchPath）一致。
    /// rg 在 Windows 输出反斜杠分隔符，因此两侧统一归一化为正斜杠后再比较前缀。
    /// </summary>
    private static string NormalizeRgPath(string line, string searchPath, string workspaceRoot)
    {
        if (string.IsNullOrEmpty(line)) return line;
        if (line.StartsWith("./", StringComparison.Ordinal) || line.StartsWith(".\\", StringComparison.Ordinal))
            line = line[2..];
        var sep = Path.DirectorySeparatorChar;

        // 若 searchPath 是具体文件，剥掉其父目录前缀，保留单文件名称
        if (File.Exists(searchPath))
        {
            var dir = Path.GetDirectoryName(searchPath);
            if (!string.IsNullOrEmpty(dir))
            {
                var dirPrefix = dir.TrimEnd(sep, '/', '\\') + sep;
                if (line.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase))
                    return line[dirPrefix.Length..];
                var dirSlashPrefix = dir.Replace('\\', '/').TrimEnd('/') + "/";
                if (line.Replace('\\', '/').StartsWith(dirSlashPrefix, StringComparison.OrdinalIgnoreCase))
                    return line[dirSlashPrefix.Length..];

                try
                {
                    var relDir = Path.GetRelativePath(workspaceRoot, dir).Replace('\\', '/');
                    if (relDir != ".")
                    {
                        var relPrefix = relDir.TrimEnd('/') + "/";
                        var normalisedLine = line.Replace('\\', '/');
                        if (normalisedLine.StartsWith(relPrefix, StringComparison.OrdinalIgnoreCase))
                            return normalisedLine[relPrefix.Length..];
                    }
                }
                catch (Exception) { /* 跨盘符等异常场景保留原样 */ }
            }
            return line;
        }

        // rg 输出绝对路径的场景（如搜索目标是显式文件）：剥掉 searchPath 前缀（双分隔符兼容）。
        var absPrefix = searchPath.TrimEnd(sep, '/', '\\') + sep;
        if (line.StartsWith(absPrefix, StringComparison.OrdinalIgnoreCase))
            return line[absPrefix.Length..];
        var absSlashPrefix = searchPath.Replace('\\', '/').TrimEnd('/') + "/";
        if (line.Replace('\\', '/').StartsWith(absSlashPrefix, StringComparison.OrdinalIgnoreCase))
            return line[absSlashPrefix.Length..];

        try
        {
            var relBase = Path.GetRelativePath(workspaceRoot, searchPath).Replace('\\', '/');
            if (relBase != ".")
            {
                var relPrefix = relBase.TrimEnd('/') + "/";
                // line 与前缀均归一化为 `/` 后比较：修复 Windows 下 rg 输出 `src\...` 无法匹配 `src/` 前缀的问题。
                var normalisedLine = line.Replace('\\', '/');
                if (normalisedLine.StartsWith(relPrefix, StringComparison.OrdinalIgnoreCase))
                    return normalisedLine[relPrefix.Length..];
            }
        }
        catch (Exception)
        {
            // 跨盘符等无法计算相对路径的场景：保留 rg 原样输出。
        }
        return line;
    }

    private async Task<List<string>> SearchNativeAsync(
        TextSearchRequest request, WorkspaceIgnoreSnapshot ignore,
        int contextBefore, int contextAfter, CancellationToken ct)
    {
        List<string> results = [];
        var regexOptions = request.CaseInsensitive ? RegexOptions.IgnoreCase | RegexOptions.Compiled : RegexOptions.Compiled;
        if (request.Multiline) regexOptions |= RegexOptions.Singleline;
        Regex regex;
        try { regex = new Regex(request.Pattern, regexOptions, RegexTimeout); }
        catch (ArgumentException ex) { return new List<string> { $"Invalid regex: {ex.Message}" }; }

        var excludePatterns = string.IsNullOrEmpty(request.ExcludeGlob)
            ? []
            : request.ExcludeGlob.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var workspaceRoot = request.WorkspaceRoot ?? request.SearchPath;
        var isSingleFile = File.Exists(request.SearchPath);
        var effectiveRoot = PathBoundary.IsWithinDirectory(request.SearchPath, workspaceRoot)
            ? workspaceRoot
            : (isSingleFile
                ? (Path.GetDirectoryName(request.SearchPath) ?? request.SearchPath)
                : request.SearchPath);
        List<string> files;

        if (isSingleFile)
        {
            // 单文件搜索：直接检查 ignore 规则，无需且不能调用 FindFiles 目录枚举
            var relToWs = GetRelativePath(request.SearchPath, effectiveRoot);
            if (ignore.IsIgnored(relToWs))
                return [];
            files = [request.SearchPath];
        }
        else
        {
            files = fileSystem.FindFiles(request.SearchPath, request.Glob, DefaultExcludes, request.AdditionalDirectories)
                .Where(file => !ignore.IsIgnored(GetRelativePath(file, effectiveRoot)))
                .ToList();
        }

        if (excludePatterns.Length > 0)
        {
            files = files.Where(f =>
            {
                var rel = GetRelativePath(f, request.SearchPath).Replace('\\', '/');
                return !excludePatterns.Any(ep => IsGlobMatch(rel, ep));
            }).ToList();
        }

        var baseDirForRelPath = isSingleFile
            ? (Path.GetDirectoryName(request.SearchPath) ?? request.SearchPath)
            : request.SearchPath;

        foreach (var file in files)
        {
            try
            {
                var relativePath = GetRelativePath(file, baseDirForRelPath);
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
                    // Count matching LINES, matching ripgrep's `-c` (which counts matching lines,
                    // not occurrences). Counting occurrences here made the two engines report
                    // different numbers for the same file, so a caller could not tell whether a
                    // result changed because the code changed or because the engine changed.
                    var count = 0;
                    await foreach (var line in File.ReadLinesAsync(file, ct).ConfigureAwait(false))
                    {
                        if (regex.IsMatch(line)) count++;
                    }
                    if (count > 0) results.Add($"{relativePath}:{count}");
                    continue;
                }
                if (contextBefore == 0 && contextAfter == 0)
                {
                    var lineIndex = 0;
                    await foreach (var line in File.ReadLinesAsync(file, ct).ConfigureAwait(false))
                    {
                        lineIndex++;
                        var match = regex.Match(line);
                        if (match.Success)
                        {
                            var formattedLine = TruncateLongLine(line, match.Index);
                            results.Add($"{relativePath}:{lineIndex}:{formattedLine}");
                        }
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

        List<string> results = [];
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
            results.Add($"{relativePath}:{lineNum}:{TruncateLongLine(snippet, 0)}");
        }
        return results;
    }

    private static async Task<List<string>> SearchNativeWithContextAsync(
        string file, string relativePath, Regex regex, int contextBefore, int contextAfter,
        CancellationToken ct)
    {
        var allLines = await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false);
        var matchLineNums = new Dictionary<int, int>();

        for (var i = 0; i < allLines.Length; i++)
        {
            var match = regex.Match(allLines[i]);
            if (match.Success)
                matchLineNums[i] = match.Index;
        }

        List<string> results = [];
        if (matchLineNums.Count == 0) return results;

        var ranges = BuildContextRanges(matchLineNums.Keys, contextBefore, contextAfter, allLines.Length);

        var firstGroup = true;
        foreach (var (start, end) in ranges)
        {
            if (!firstGroup) results.Add("--");
            firstGroup = false;

            for (var i = start; i <= end; i++)
            {
                var lineNum = i + 1;
                var isMatch = matchLineNums.TryGetValue(i, out var matchIdx);
                var sep = isMatch ? ":" : "-";
                var formattedLine = TruncateLongLine(allLines[i], isMatch ? matchIdx : 0);
                results.Add($"{relativePath}{sep}{lineNum}{sep}{formattedLine}");
            }
        }
        return results;
    }

    internal static string TruncateLongLine(string line, int matchIndex = 0)
    {
        if (string.IsNullOrEmpty(line) || line.Length <= MaxLineColumns)
            return line;

        var halfWindow = MaxLineColumns / 2;
        var start = Math.Clamp(matchIndex - halfWindow, 0, Math.Max(0, line.Length - MaxLineColumns));
        var length = Math.Min(MaxLineColumns, line.Length - start);
        var prefix = start > 0 ? "..." : string.Empty;
        return $"{prefix}{line.Substring(start, length)}{LineTruncatedSuffix}";
    }

    private static List<(int Start, int End)> BuildContextRanges(
        IEnumerable<int> matchLines, int before, int after, int totalLines)
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
