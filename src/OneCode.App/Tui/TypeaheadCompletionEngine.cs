using System.Text.RegularExpressions;

namespace OneCode.App.Tui;

/// <summary>
/// 为 REPL 输入框提供实时补全建议，支持四种补全模式：
///   1. 会话 ID（<c>/session switch 4f</c>、<c>/close 4f</c> → 补全完整 32 位 ID）
///   2. 斜杠命令（<c>/command</c> → <c>/config</c>、<c>/help</c> 等）
///   3. <c>@</c> 工作区文件关联（<c>@</c> 或 <c>@query</c> → 扁平列出工作区文件相对路径并实时模糊过滤）
///   4. 普通文件路径（<c>read "path</c> → <c>read "path/to/file.cs"</c>）
/// </summary>
public sealed partial class TypeaheadCompletionEngine(
    IReadOnlyList<SlashCommandEntry> commands,
    string workingDirectory,
    Func<IReadOnlyList<OneCode.App.Session.SessionCandidate>>? sessionCandidatesProvider = null)
{
    private const int MaxAtFileResults = 15;
    private const int MaxScannedFiles = 2000;
    private const int MaxScanDepth = 6;

    private IReadOnlyList<SlashCommandEntry> _commands = commands;
    private readonly string _workingDirectory = workingDirectory;
    private readonly Func<IReadOnlyList<OneCode.App.Session.SessionCandidate>>? _sessionCandidatesProvider = sessionCandidatesProvider;

    /// <summary>
    /// 运行时替换斜杠命令列表（如技能或 MCP 动态命令刷新后）。
    /// </summary>
    public void UpdateCommands(IReadOnlyList<SlashCommandEntry> commands)
        => _commands = commands;

    /// <summary>
    /// 检测当前输入的补全模式并返回匹配的插入文本列表；无匹配时返回空列表。
    /// </summary>
    public List<string> GetCompletions(string input)
    {
        if (string.IsNullOrEmpty(input))
            return [];

        // 模式 0：会话 ID 参数（/session switch|delete <prefix>、/close <prefix>）。
        if (TryGetSessionIdItems(input, out var sessionItems))
            return sessionItems.Select(i => i.Insert).ToList();

        // 模式 1：斜杠命令——检查最后一个词，支持行中触发（如 "hello /help"）。
        var lastWord = ExtractLastWord(input);
        if (lastWord.StartsWith('/'))
            return GetCommandCompletions(lastWord);

        // 模式 2：@ 工作区文件关联补全（裸 @ 或 @<查询词>）
        var lastAt = input.LastIndexOf('@');
        if (lastAt >= 0)
        {
            var prefix = input[..lastAt];
            var query = input[(lastAt + 1)..];
            if (!query.Contains(' '))
            {
                var fileMatches = GetAtFileCompletions(prefix, query);
                if (fileMatches.Count > 0)
                    return fileMatches;
            }
        }

        // 模式 3：普通文件路径（包含引号或路径分隔符特征时）
        if (TryGetFilePathCompletion(input, out var pathCompletions))
            return pathCompletions;

        return [];
    }

    /// <summary>
    /// 返回补全弹窗的（展示文本, 插入文本）元组列表。
    /// </summary>
    public List<(string Display, string Insert)> GetCompletionItems(string input)
    {
        // 会话 id 模式的展示文本（短 id + 标题）无法从插入文本反推，须成对产出。
        // 以当前 input 现场解析为准：不经缓存转发，输入切到其他模式时不会拿到
        // 上一个输入残留的会话候选（GetCompletions 与本方法可各自独立调用）。
        if (TryGetSessionIdItems(input, out var sessionItems))
            return sessionItems;

        var completions = GetCompletions(input);
        return completions.Select(c => (ExtractDisplayText(input, c), c)).ToList();
    }

    private bool TryGetSessionIdItems(string input, out List<(string Display, string Insert)> items)
    {
        items = [];
        if (_sessionCandidatesProvider is null)
            return false;

        var match = SessionIdArgumentRegex().Match(input);
        if (!match.Success)
            return false;

        var idPrefix = match.Groups["id"].Value;
        var commandPart = input[..^idPrefix.Length];

        items = _sessionCandidatesProvider.Invoke()
            .Where(c => string.IsNullOrEmpty(idPrefix)
                || c.FullId.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(c => (
                Display: $"{c.ShortId}  {c.Title}  ({c.Mode}, {c.MessageCount} msgs)",
                Insert: $"{commandPart}{c.FullId} "))
            .ToList();

        return items.Count > 0;
    }

    // 触发形态：命令 + 空白 + 可空 id 前缀，且 id 参数位于行尾（光标即行尾）。
    [GeneratedRegex(@"^/(?:session (?:switch|delete)|close)\s+(?<id>\S*)$")]
    private static partial Regex SessionIdArgumentRegex();

    internal static bool IsSessionIdCompletionInput(string input)
        => !string.IsNullOrEmpty(input) && SessionIdArgumentRegex().IsMatch(input);

    internal static bool IsSlashCommandCompletionInput(string input)
        => !string.IsNullOrEmpty(input) && !IsSessionIdCompletionInput(input) && ExtractLastWord(input).StartsWith('/');

    // 与 GetCompletions 的模式判定顺序一致：会话 ID / 斜杠命令优先，其余看最后一个 @。
    internal static bool IsAtFileInput(string input)
        => TryExtractAtFileQuery(input, out _);

    internal static bool TryExtractAtFileQuery(string input, out string query)
    {
        query = string.Empty;
        if (string.IsNullOrEmpty(input)
            || IsSessionIdCompletionInput(input)
            || ExtractLastWord(input).StartsWith('/'))
        {
            return false;
        }

        var lastAt = input.LastIndexOf('@');
        if (lastAt < 0)
            return false;

        var candidate = input[(lastAt + 1)..];
        if (candidate.Contains(' '))
            return false;

        query = candidate;
        return true;
    }

    private static string ExtractDisplayText(string input, string completion)
    {
        if (completion.StartsWith('/'))
            return completion;

        // @ 文件补全只展示最后一个 @ 之后的相对路径（去掉插入文本末尾附带的空格）
        var lastAt = completion.LastIndexOf('@');
        if (lastAt >= 0)
            return completion[(lastAt + 1)..].TrimEnd();

        if (completion.StartsWith('"'))
        {
            var endQuote = completion.LastIndexOf('"');
            if (endQuote > 0)
                return completion[1..endQuote];
        }

        if (input.Length > 0 && completion.StartsWith(input, StringComparison.Ordinal))
        {
            var suffix = completion[input.Length..];
            if (!string.IsNullOrEmpty(suffix))
                return suffix;
        }

        return completion;
    }

    private List<string> GetCommandCompletions(string input)
    {
        var query = input.TrimStart('/').ToLowerInvariant();

        // 第一阶段：命令名前缀精确匹配优先，避免 /status 因描述包含单词而误命中 /hooks。
        var nameMatches = _commands
            .Where(c => string.IsNullOrEmpty(query)
                || c.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .Select(c => $"/{c.Name}")
            .ToList();

        if (nameMatches.Count > 0)
            return nameMatches;

        // 第二阶段：仅当无命令名前缀命中时，回退到描述整词匹配。
        if (string.IsNullOrEmpty(query))
            return _commands.Select(c => $"/{c.Name}").ToList();

        var descMatches = _commands
            .Where(c => c.Description.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(word => word.Equals(query, StringComparison.OrdinalIgnoreCase)))
            .Select(c => $"/{c.Name}")
            .ToList();

        return descMatches;
    }

    /// <summary>
    /// 扁平列出工作区文件相对路径并按查询词实时模糊过滤（只返回文件，不返回目录，附带尾随空格以便一次回车选中）。
    /// </summary>
    private List<string> GetAtFileCompletions(string prefix, string query)
    {
        try
        {
            if (!Directory.Exists(_workingDirectory))
                return [];

            var includeHiddenFiles = query.StartsWith('.')
                || query.Contains("/.", StringComparison.Ordinal)
                || query.Contains("\\.", StringComparison.Ordinal);

            if (string.IsNullOrEmpty(query))
            {
                return EnumerateWorkspaceFiles(includeHiddenFiles)
                    .Take(MaxAtFileResults)
                    .Select(f => $"{prefix}@{f.RelPath} ")
                    .ToList();
            }

            var normalizedQuery = query.Replace('\\', '/').TrimStart('.', '/');
            var isPathQuery = query.Contains('/') || query.Contains('\\');
            var matches = new List<(string RelPath, string FileName, int Depth, int Tier)>();

            foreach (var (relPath, fileName, depth) in EnumerateWorkspaceFiles(includeHiddenFiles))
            {
                var normalizedRel = relPath.Replace('\\', '/');
                var tier = MatchWorkspaceFileTier(query, normalizedQuery, isPathQuery, fileName, normalizedRel);
                if (tier > 0)
                    matches.Add((relPath, fileName, depth, tier));
            }

            return matches
                .OrderBy(m => m.Tier)
                .ThenBy(m => m.Depth)
                .ThenBy(m => m.FileName.Length)
                .ThenBy(m => m.RelPath, StringComparer.OrdinalIgnoreCase)
                .Take(MaxAtFileResults)
                .Select(m => $"{prefix}@{m.RelPath} ")
                .ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to enumerate workspace files for @ completion: {ex.Message}");
            return [];
        }
    }

    private static int MatchWorkspaceFileTier(
        string query,
        string normalizedQuery,
        bool isPathQuery,
        string fileName,
        string normalizedRel)
    {
        if (!isPathQuery && fileName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 1;
        if (normalizedQuery.Length > 0 && normalizedRel.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            return 2;
        if (!isPathQuery && fileName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 3;
        if (normalizedQuery.Length > 0 && normalizedRel.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            return 4;
        if (!isPathQuery && query.Length >= 2 && IsFuzzySubsequence(query, fileName))
            return 5;
        return 0;
    }

    private static readonly HashSet<string> IgnoredFuzzyDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea", "dist", "out", "artifacts",
    };

    private IEnumerable<(string RelPath, string FileName, int Depth)> EnumerateWorkspaceFiles(bool includeHiddenFiles)
    {
        var queue = new Queue<(string FullDir, int Depth)>();
        queue.Enqueue((_workingDirectory, 0));
        var scannedFiles = 0;

        while (queue.Count > 0 && scannedFiles < MaxScannedFiles)
        {
            var (currentDir, depth) = queue.Dequeue();
            if (depth > MaxScanDepth)
                continue;

            string[] files;
            string[] subDirs;
            try
            {
                files = Directory.GetFiles(currentDir);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                subDirs = depth < MaxScanDepth ? Directory.GetDirectories(currentDir) : [];
                if (subDirs.Length > 1)
                    Array.Sort(subDirs, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Skipped inaccessible directory '{currentDir}': {ex.Message}");
                continue;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (!includeHiddenFiles && name.StartsWith('.'))
                    continue;

                scannedFiles++;
                if (scannedFiles > MaxScannedFiles)
                    break;

                var relPath = Path.GetRelativePath(_workingDirectory, file);
                yield return (relPath, name, depth);
            }

            foreach (var subDir in subDirs)
            {
                var dirName = Path.GetFileName(subDir);
                if (!dirName.StartsWith('.') && !IgnoredFuzzyDirs.Contains(dirName))
                    queue.Enqueue((subDir, depth + 1));
            }
        }
    }

    private static bool IsFuzzySubsequence(string query, string candidate)
    {
        var qi = 0;
        for (var ci = 0; ci < candidate.Length && qi < query.Length; ci++)
        {
            if (char.ToLowerInvariant(candidate[ci]) == char.ToLowerInvariant(query[qi]))
                qi++;
        }
        return qi == query.Length;
    }

    private bool TryGetFilePathCompletion(string input, out List<string> completions)
    {
        completions = [];

        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return false;

        var lastWord = words[^1];

        // 以 '/' 开头的词属于斜杠命令，不作为文件路径处理。
        if (lastWord.StartsWith('/'))
            return false;

        var isInQuotes = lastWord.StartsWith('"') && !lastWord.EndsWith('"');
        var isPathLike = lastWord.Contains('/') || lastWord.Contains('\\')
            || lastWord.StartsWith('.') || lastWord.StartsWith('~');

        if (!isInQuotes && !isPathLike)
            return false;

        var pathPrefix = isInQuotes ? lastWord[1..] : lastWord;

        try
        {
            var dir = Path.GetDirectoryName(pathPrefix);
            if (string.IsNullOrEmpty(dir))
                dir = _workingDirectory;
            else if (!Path.IsPathRooted(dir))
                dir = Path.Combine(_workingDirectory, dir);

            if (!Directory.Exists(dir))
                return false;

            var fileName = Path.GetFileName(pathPrefix);
            var entries = Directory.GetFileSystemEntries(dir)
                .Select(p => Path.GetFileName(p))
                .Where(f => f.StartsWith(fileName, StringComparison.OrdinalIgnoreCase))
                .Take(20)
                .ToList();

            if (entries.Count == 0)
                return false;

            var prefixDir = Path.GetDirectoryName(pathPrefix) ?? "";
            if (prefixDir.Length > 0)
                prefixDir += Path.DirectorySeparatorChar;

            completions = entries.Select(f =>
            {
                var full = prefixDir + f;
                var fullPath = Path.Combine(_workingDirectory, full);
                var suffix = Directory.Exists(fullPath) ? Path.DirectorySeparatorChar.ToString() : "";
                return isInQuotes ? $"\"{full}{suffix}\"" : $"{full}{suffix}";
            }).ToList();

            return completions.Count > 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to complete file path '{pathPrefix}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 提取输入中最后一个以空格分隔的词（保留双引号包裹的片段）。
    /// </summary>
    private static string ExtractLastWord(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var inQuotes = false;
        var lastSpace = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') inQuotes = !inQuotes;
            else if (text[i] == ' ' && !inQuotes) lastSpace = i;
        }
        return lastSpace >= 0 ? text[(lastSpace + 1)..] : text;
    }
}
