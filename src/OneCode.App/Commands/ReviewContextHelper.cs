using System.Text;
using OneCode.Infrastructure.Lsp;
using OneCode.App.Services.Lsp;
using OneCode.Core.Lsp;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Git;

namespace OneCode.App.Commands;

/// <summary>
/// <see cref="ReviewCommand"/> 的上下文采集与 diff 分块截断辅助：
/// 负责解析 diff 变更文件、分层收集祖先目录 AGENTS.md、聚合 LSP 诊断、获取 git blame 及整文件级 diff 截断。
/// </summary>
internal static class ReviewContextHelper
{
    public static IReadOnlyList<string> ParseChangedFilesFromDiff(string diff)
    {
        List<string> files = [];
        foreach (var line in diff.Split('\n'))
        {
            if (!line.StartsWith("+++ b/", StringComparison.Ordinal)) continue;
            var path = line["+++ b/".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(path) && path != "/dev/null")
                files.Add(path);
        }
        return files;
    }

    public static async Task<string?> TryGatherBlameContextAsync(
        string diff,
        string workingDirectory,
        IGitHelper gitHelper,
        ILogger? logger,
        CancellationToken ct)
    {
        try
        {
            var changedFiles = ParseChangedFilesFromDiff(diff);
            if (changedFiles.Count == 0) return null;

            var baseDir = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory;
            var blameResults = await Task.WhenAll(changedFiles.Take(5)
                .Where(file =>
                {
                    var resolved = PathsHelper.SafeResolve(file, baseDir);
                    return resolved.IsSuccess && File.Exists(resolved.Value);
                })
                .Select(async file => (File: file, Result: await gitHelper.RunAsync(
                    ["blame", "--porcelain", file], ct).ConfigureAwait(false))))
                .ConfigureAwait(false);

            var sb = new StringBuilder();
            foreach (var (file, result) in blameResults)
            {
                if (result is null || string.IsNullOrWhiteSpace(result.Stdout)) continue;

                var entries = GitBlameParser.Parse(result.Stdout, file);
                sb.AppendLine(CultureInfo.InvariantCulture, $"### {file}");
                foreach (var entry in entries.Take(50))
                {
                    sb.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"  L{entry.LineNumber} [{entry.CommitHash[..8]}] {entry.AuthorName} — {entry.Content}");
                }
                sb.AppendLine();
            }

            return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
        }
        catch (Exception ex)
        {
            if (logger is not null)
                logger.LogWarning(ex, "ReviewCommand.BuildGitBlameContext failed");
            else
                System.Diagnostics.Debug.WriteLine($"ReviewCommand.BuildGitBlameContext failed: {ex.Message}");
            return null;
        }
    }

    public static async Task<string?> TryReadAgentsMdAsync(
        string? repoRoot,
        IReadOnlyList<string> changedFiles,
        int maxFiles,
        int maxCharsPerFile,
        ILogger? logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || changedFiles.Count == 0) return null;
        try
        {
            var rootPath = Path.GetFullPath(repoRoot);
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in changedFiles)
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(rootPath, file)));
                while (dir is not null
                    && dir.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)
                    && dirs.Add(dir))
                {
                    dir = Path.GetDirectoryName(dir);
                }
            }

            var selected = dirs
                .OrderByDescending(d => d.Length)
                .Take(maxFiles)
                .OrderBy(d => d.Length);

            var sections = new List<string>();
            foreach (var dir in selected)
            {
                var path = Path.Combine(dir, "AGENTS.md");
                if (!File.Exists(path)) continue;
                var content = (await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)).Trim();
                if (content.Length == 0) continue;
                if (content.Length > maxCharsPerFile)
                    content = content[..maxCharsPerFile] + "\n…(truncated)";
                var relative = Path.GetRelativePath(rootPath, path).Replace('\\', '/');
                sections.Add($"### {relative}\n{content}");
            }

            return sections.Count > 0 ? string.Join("\n\n", sections) : null;
        }
        catch (Exception ex)
        {
            if (logger is not null)
                logger.LogWarning(ex, "ReviewCommand.LoadReviewContext failed for {RepoRoot}", repoRoot);
            else
                System.Diagnostics.Debug.WriteLine($"ReviewCommand.LoadReviewContext failed: {ex.Message}");
            return null;
        }
    }

    public static string? TryGatherLspDiagnostics(
        LspDiagnosticRegistry lspRegistry,
        IReadOnlyList<string> changedFiles,
        string? repoRoot,
        ILogger? logger)
    {
        try
        {
            if (changedFiles.Count == 0) return null;

            var allDiagnostics = lspRegistry.GetAllDiagnostics();
            if (allDiagnostics.Count == 0) return null;

            var changedSet = changedFiles
                .Select(cf => repoRoot is null ? cf : Path.Combine(repoRoot, cf))
                .Select(NormalizePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var relevant = allDiagnostics
                .Where(d => d.Severity is LspDiagnosticSeverity.Error or LspDiagnosticSeverity.Warning)
                .Where(d => changedSet.Contains(NormalizePath(d.FilePath)))
                .Take(200)
                .ToList();

            if (relevant.Count == 0) return null;

            var sb = new StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Existing static analysis issues for changed files ({relevant.Count} Error/Warning):");
            sb.AppendLine();

            foreach (var group in relevant.GroupBy(d => d.FilePath))
            {
                var fileName = Path.GetFileName(group.Key);
                sb.AppendLine(CultureInfo.InvariantCulture, $"### {fileName}");
                foreach (var d in group.Take(20))
                {
                    var severity = d.Severity == LspDiagnosticSeverity.Error ? "ERROR" : "WARNING";
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  [{severity}] {d.Source ?? d.ServerName}: {d.Message}");
                }
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            if (logger is not null)
                logger.LogWarning(ex, "ReviewCommand: LSP diagnostics aggregation failed");
            else
                System.Diagnostics.Debug.WriteLine($"ReviewCommand LSP diagnostics aggregation failed: {ex.Message}");
            return null;
        }
    }

    public static string CapDiffForPrompt(string diff, int maxDiffPromptChars)
    {
        if (diff.Length <= maxDiffPromptChars) return diff;

        var files = SplitDiffByFile(diff);
        var builder = new StringBuilder();
        var budget = maxDiffPromptChars;
        var included = 0;
        foreach (var (_, chunk) in files)
        {
            if (chunk.Length > budget) break;
            builder.Append(chunk);
            budget -= chunk.Length;
            included++;
        }

        if (included == 0 && files.Count > 0)
        {
            builder.Append(files[0].Chunk.AsSpan(0, Math.Min(files[0].Chunk.Length, maxDiffPromptChars)));
            included = 1;
        }

        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"[diff truncated: showing {included} of {files.Count} changed files]");
        var omitted = files.Skip(included).Select(f => f.FileName).ToList();
        foreach (var name in omitted.Take(20))
            builder.AppendLine(CultureInfo.InvariantCulture, $"  - {name}");
        if (omitted.Count > 20)
            builder.AppendLine(CultureInfo.InvariantCulture, $"  - … ({omitted.Count - 20} more omitted)");
        builder.AppendLine("[Narrow the scope: /review --staged | --base <ref> | <file-path>]");
        return builder.ToString();
    }

    public static IReadOnlyList<(string FileName, string Chunk)> SplitDiffByFile(string diff)
    {
        var chunks = new List<(string FileName, string Chunk)>();
        var start = diff.IndexOf("diff --git ", StringComparison.Ordinal);
        while (start >= 0)
        {
            var next = diff.IndexOf("diff --git ", start + 1, StringComparison.Ordinal);
            var chunk = next < 0 ? diff[start..] : diff[start..next];
            chunks.Add((ExtractDiffFileName(chunk), chunk));
            start = next;
        }
        return chunks;
    }

    private static string ExtractDiffFileName(string chunk)
    {
        const string marker = " b/";
        var firstLineEnd = chunk.IndexOf('\n');
        var firstLine = firstLineEnd < 0 ? chunk : chunk[..firstLineEnd];
        var idx = firstLine.IndexOf(marker, StringComparison.Ordinal);
        return idx < 0 ? firstLine : firstLine[(idx + marker.Length)..].Trim();
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/');
}
