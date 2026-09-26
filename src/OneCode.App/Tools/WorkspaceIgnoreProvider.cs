using Microsoft.Extensions.FileSystemGlobbing;
using OneCode.Core.IO;

namespace OneCode.App.Tools;

/// <summary>Provides the current workspace's built-in and user ignore rules.</summary>
public interface IWorkspaceIgnoreProvider
{
    Task<WorkspaceIgnoreSnapshot> GetSnapshotAsync(CancellationToken ct = default);
}

/// <summary>Immutable ignore rules loaded from one workspace.</summary>
public sealed class WorkspaceIgnoreSnapshot
{
    private readonly IReadOnlyList<IgnoreRule> _rules;

    internal WorkspaceIgnoreSnapshot(string? path, IReadOnlyList<IgnoreRule> rules, IReadOnlyList<string> diagnostics)
    {
        Path = path;
        _rules = rules;
        Diagnostics = diagnostics;
    }

    /// <summary>Gets the loaded `.onecodeignore` path, or null when it does not exist.</summary>
    public string? Path { get; }

    /// <summary>Gets parser diagnostics without exposing rule contents.</summary>
    public IReadOnlyList<string> Diagnostics { get; }

    /// <summary>Returns whether a workspace-relative path is ignored.</summary>
    public bool IsIgnored(string relativePath)
    {
        // Normalize `\`→`/`, then strip only a single leading `./` and any leading `/`.
        // 不能 TrimStart 所有前导 `.`：`.env` 是合法文件名，也是常见的忽略规则目标，
        // 字符集剥离会破坏点文件规则与 FileIgnore.Folders（`.git` 等）的段匹配。
        var normalised = relativePath.Replace('\\', '/');
        if (normalised.StartsWith("./", StringComparison.Ordinal)) normalised = normalised[2..];
        if (normalised.StartsWith("/", StringComparison.Ordinal)) normalised = normalised[1..];
        normalised = normalised.TrimEnd('/');

        if (normalised.Length == 0)
            return false;

        // 内置规则最先判定：用户规则的 `!` 不能恢复内置忽略的路径（§5.3）。
        if (FileIgnore.IsIgnored(normalised))
            return true;

        if (_rules.Count == 0)
            return false;

        // gitignore 目录剪枝：自根向下逐级做 last-match 判定；祖先目录一旦被排除，
        // 其下整体忽略（粘性，不再被更深层级的否定恢复），与 ripgrep `--ignore-file`
        // 的遍历剪枝行为一致，保证 Grep 的 rg 路径与 C# fallback 对同一 `.onecodeignore` 给出相同结果。
        var segments = normalised.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < segments.Length; i++)
        {
            var decision = LastMatch(string.Join('/', segments[..i]), directoryContext: true);
            if (decision is true)
                return true;
        }

        return LastMatch(normalised, directoryContext: false) ?? false;
    }

    /// <summary>Last-match-wins among user rules whose dir/file patterns hit <paramref name="path"/>.</summary>
    private bool? LastMatch(string path, bool directoryContext)
    {
        bool? decision = null;
        foreach (var rule in _rules)
        {
            var matcher = directoryContext ? rule.DirMatcher : rule.FileMatcher;
            if (matcher.Match(path).HasMatches)
                decision = !rule.Negated;
        }
        return decision;
    }

    /// <summary>Adds only non-negatable built-in excludes to a file matcher.</summary>
    public void ApplyBuiltInExcludes(Matcher matcher) => FileIgnore.ApplyExcludes(matcher);

    /// <summary>
    /// One parsed `.onecodeignore` line. <see cref="DirMatcher"/> matches directory entries
    /// （用于 gitignore 目录剪枝）；<see cref="FileMatcher"/> matches file paths——目录规则
    /// （末尾 `/`）只贡献其“目录之下”模式，因此恰好与目录同名的文件不会被该规则忽略。
    /// </summary>
    internal sealed record IgnoreRule(Matcher DirMatcher, Matcher FileMatcher, bool Negated)
    {
        /// <summary>
        /// 将一行 gitignore 风格规则翻译为 Dir/File 两组 include 模式。
        /// 锚定判定以“去掉首尾 `/` 后中间是否含 `/`”为准：前导 `/` 或中间 `/` 都锚定到
        /// 工作区根（`/foo` 与 `a/b` 都不匹配 `src/foo`），否则规则在任意层级生效
        /// （`cache/` 必须同时命中 `cache/**` 与 `src/cache/**`）；末尾 `/` 仅表示目录规则。
        /// 无锚定模式同时生成裸模式与 `**/` 前缀模式，覆盖根级与嵌套命中
        /// （与 FileIgnore.ApplyExcludes 的既有实践一致）。
        /// </summary>
        public static IgnoreRule Parse(string line, bool negated)
        {
            var pattern = line.Replace('\\', '/');
            var anchored = pattern.StartsWith('/');
            var dirOnly = pattern.Length > 1 && pattern.EndsWith('/');
            pattern = pattern.Trim('/');
            if (pattern.Length == 0)
                throw new ArgumentException("empty pattern", nameof(line));

            var anchoredToRoot = anchored || pattern.Contains('/');
            string[] entry, under;
            if (dirOnly)
            {
                entry = anchoredToRoot ? [pattern] : [pattern, $"**/{pattern}"];
                under = anchoredToRoot ? [$"{pattern}/**"] : [$"{pattern}/**", $"**/{pattern}/**"];
            }
            else if (pattern.EndsWith("/**", StringComparison.Ordinal))
            {
                // `data/**` 只排除 data 目录之下的内容，不排除 data 目录本身——
                // 因此 `!data/keep.txt` 仍然生效（gitignore 语义）。
                entry = [];
                under = [pattern];
            }
            else if (anchoredToRoot)
            {
                entry = [pattern];
                under = [$"{pattern}/**"];
            }
            else
            {
                entry = [pattern, $"**/{pattern}"];
                under = [$"{pattern}/**", $"**/{pattern}/**"];
            }

            var dirMatcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            foreach (var p in entry.Concat(under))
                dirMatcher.AddInclude(p);

            var fileMatcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            foreach (var p in dirOnly ? under : entry.Concat(under))
                fileMatcher.AddInclude(p);

            return new IgnoreRule(dirMatcher, fileMatcher, negated);
        }
    }
}

/// <summary>Loads and caches `.onecodeignore` for the current working directory.</summary>
public sealed class WorkspaceIgnoreProvider(
    IWorkingDirectoryAccessor workingDirectory,
    IFileSystem fileSystem) : IWorkspaceIgnoreProvider
{
    private readonly object _gate = new();
    private string? _cachedPath;
    private long _cachedMtime;
    private WorkspaceIgnoreSnapshot? _cachedSnapshot;

    public async Task<WorkspaceIgnoreSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var root = Path.GetFullPath(workingDirectory.WorkingDirectory);
        var path = Path.Combine(root, ".onecodeignore");
        var mtime = fileSystem.GetMtimeMs(path);
        lock (_gate)
        {
            if (_cachedSnapshot is not null &&
                string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase) &&
                _cachedMtime == mtime)
                return _cachedSnapshot;
        }

        var content = await fileSystem.ReadTextFileAsync(path, ct).ConfigureAwait(false);
        var snapshot = Parse(path, content);
        lock (_gate)
        {
            _cachedPath = path;
            _cachedMtime = mtime;
            _cachedSnapshot = snapshot;
            return snapshot;
        }
    }

    private static WorkspaceIgnoreSnapshot Parse(string path, string? content)
    {
        if (content is null)
            return new WorkspaceIgnoreSnapshot(null, [], []);

        List<WorkspaceIgnoreSnapshot.IgnoreRule> rules = [];
        List<string> diagnostics = [];
        var lineNumber = 0;
        foreach (var rawLine in content.Split(['\r', '\n'], StringSplitOptions.None))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var negated = line[0] == '!';
            if (negated) line = line[1..].TrimStart();
            if (line.Length == 0)
            {
                diagnostics.Add($"Line {lineNumber}: empty negation rule");
                continue;
            }

            try
            {
                rules.Add(WorkspaceIgnoreSnapshot.IgnoreRule.Parse(line, negated));
            }
            catch (ArgumentException ex)
            {
                diagnostics.Add($"Line {lineNumber}: {ex.Message}");
            }
        }

        return new WorkspaceIgnoreSnapshot(path, rules, diagnostics);
    }
}
