using System.ComponentModel;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Remote;
using OneCode.Infrastructure.Text;

namespace OneCode.App.Tools;

/// <summary>
/// Performs search-and-replace edits on files, mirroring the TypeScript Edit tool.
///
/// Key semantics (matching TypeScript behavior):
///  - Requires EXACTLY one occurrence of oldString in the file (errors if 0 or >1)
///  - Replaces only the first occurrence (deterministic even when count==1)
///  - Preserves the file's original line-ending style (CRLF / LF)
///  - Preserves the file's BOM/encoding
/// </summary>
/// <remarks>
/// 可选依赖（保留可空）：
/// - <c>notifier</c>（构造参数）：LSP 通知器；生产环境由 DI 注入 <see cref="LspNotifier"/>
/// - <see cref="_ssh"/>：SSH 远程编辑；仅在配置远程连接时非空，缺失时走本地文件系统
/// </remarks>
public sealed class EditTool
{
    private readonly ILspNotifier _notifier;
    private readonly IWorkingDirectoryAccessor _wd;
    private readonly SshRemoteService _ssh;
    private readonly ILogger<EditTool>? _logger;

    public EditTool(
        ILspNotifier notifier,
        IWorkingDirectoryAccessor wd,
        SshRemoteService ssh,
        ILogger<EditTool>? logger = null)
    {
        _notifier = notifier;
        _wd = wd;
        _ssh = ssh;
        _logger = logger;
    }

    [Description("Perform a search-and-replace edit on a file. Safer than Write for targeted modifications because it preserves surrounding content. " +
                 "Uniqueness contract: by default oldString MUST appear exactly once in the file — the call errors on 0 matches (typo/whitespace mismatch) or >1 matches (provide more context to disambiguate). " +
                 "Set replaceAll=true to replace EVERY occurrence instead (still errors on 0 matches) and get the replaced count back; replaceAll is only valid with mode='replace'. " +
                 "Three modes: 'replace' (default, swap oldString with newString), 'insert_after' (insert newString after the anchor without removing it), 'insert_before' (insert before the anchor). " +
                 "Encoding preservation: the file's original BOM/encoding (UTF-8 BOM, UTF-16 LE/BE) and line-ending style (CRLF/LF) are detected and preserved; caller-supplied \\n is normalized to match. " +
                 "Path safety: must resolve within the working directory; .git/ and sensitive paths are hard-blocked by FileSystemInvariant. " +
                 "LSP integration: after a successful edit, the LSP server is notified and a diagnostics summary is returned. " +
                 "dryRun=true returns a unified diff preview without touching disk. " +
                 "Tip: include enough surrounding lines in oldString to guarantee uniqueness; use replaceAll for renames/bulk replacements instead of one call per occurrence.")]
    public async Task<ToolResult> EditAsync(
        [Description("Absolute or relative path of the file to edit. Must already exist; use Write to create new files.")] string filePath,
        [Description("The text to search for. By default must appear EXACTLY once in the file (errors on 0 or >1 matches); with replaceAll=true every occurrence is replaced. Include enough surrounding lines to be unique. Whitespace and indentation must match exactly.")] string oldString,
        [Description("The replacement text (for mode='replace') or the text to insert (for insert_after / insert_before modes). May be empty to delete the anchor.")] string newString,
        [Description("Edit mode: 'replace' (default, swap oldString -> newString), 'insert_after' (insert newString after the anchor, keep anchor), 'insert_before' (insert before the anchor, keep anchor).")] string mode = "replace",
        [Description("When true, replace every occurrence of oldString instead of requiring a unique match. Only valid with mode='replace'; errors on 0 occurrences.")] bool replaceAll = false,
        [Description("When true, return a unified diff preview without modifying the file. Use to verify the change before committing.")] bool dryRun = false,
        CancellationToken ct = default)
    {
        try
        {
            if (replaceAll && mode is not "replace")
                return ToolResult.Error("Error: replaceAll is only valid with mode='replace'. " +
                       "Combining replaceAll with insert_after/insert_before is ambiguous and not supported.");

            if (_ssh is { IsConnected: true } ssh)
                return await EditRemoteAsync(ssh, filePath, oldString, newString, mode, replaceAll, dryRun, ct).ConfigureAwait(false);

            var resolved = PathsHelper.SafeResolve(filePath, _wd.WorkingDirectory, _wd.AdditionalDirectories);
            if (!resolved.IsSuccess)
                return ToolResult.Error($"Error: {resolved.Error}");
            var fullPath = resolved.Value!;

            if (!File.Exists(fullPath))
                return ToolResult.Error(BuildMissingFileMessage(fullPath, _wd.WorkingDirectory));

            // Guard against OOM on large files — Edit requires reading the entire file
            var fileSize = new FileInfo(fullPath).Length;
            if (fileSize > PathsHelper.MaxFileReadSize)
                return ToolResult.Error($"Error: File is too large to edit ({fileSize / 1024 / 1024}MB). " +
                       $"Maximum supported size is {PathsHelper.MaxFileReadSize / 1024 / 1024}MB. " +
                       "Consider using a different tool or splitting the file.");

            var (content, encoding, lineEndingStyle) =
                await FileEncodingHelper.ReadWithEncodingAsync(fullPath, ct);

            var searchFor = FileEncodingHelper.NormalizeLineEndings(oldString, lineEndingStyle);
            var insertText = FileEncodingHelper.NormalizeLineEndings(newString, lineEndingStyle);

            // Count occurrences — require exactly 1 unless replaceAll replaces every match
            var count = CountOccurrences(content, searchFor);
            if (count == 0)
            {
                // Also try raw (no normalisation) in case the caller already used CRLF
                if (oldString != searchFor && CountOccurrences(content, oldString) > 0)
                {
                    searchFor = oldString;
                    insertText = newString;
                    count = CountOccurrences(content, searchFor);
                }
            }

            switch (count)
            {
                case 0:
                    return ToolResult.Error($"Error: Could not find the specified text in {fullPath}. " +
                           "Make sure oldString matches the file content exactly (including whitespace).");
                case > 1 when !replaceAll:
                    return ToolResult.Error($"Error: Found {count} occurrences of oldString in {fullPath}. " +
                           "Please provide a more specific (unique) oldString, or set replaceAll=true to replace every occurrence.");
            }

            var newContent = replaceAll
                ? content.Replace(searchFor, insertText, StringComparison.Ordinal)
                : ReplaceSingle(content, searchFor, insertText, mode);

            if (dryRun)
            {
                var diff = UnifiedDiff.Compute(content, newContent, filePath);
                return ToolResult.Success(diff);
            }

            await FileEncodingHelper.WriteWithEncodingAsync(fullPath, newContent, encoding, ct);

            var successMessage = replaceAll
                ? $"File updated: {fullPath} ({count} {(count == 1 ? "occurrence" : "occurrences")} replaced)"
                : $"File updated: {fullPath}";
            var message = await FileWritePipeline.CompleteWriteAsync(
                fullPath, newContent, _notifier,
                successMessage, ct).ConfigureAwait(false);
            return ToolResult.Success(message);
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Error editing {filePath}: {ex.Message}");
        }
    }

    private async Task<ToolResult> EditRemoteAsync(
        SshRemoteService ssh,
        string filePath,
        string oldString,
        string newString,
        string mode,
        bool replaceAll,
        bool dryRun,
        CancellationToken ct)
    {
        var content = await ssh.ReadFileAsync(filePath, ct).ConfigureAwait(false);
        if (content is null)
            return ToolResult.Error($"Error: File not found over SSH: {filePath}");

        var count = CountOccurrences(content, oldString);
        if (count == 0)
            return ToolResult.Error($"Error: Could not find the specified text in ssh:{filePath}.");
        if (count > 1 && !replaceAll)
            return ToolResult.Error($"Error: Found {count} occurrences of oldString in ssh:{filePath}. Please provide a more specific oldString, or set replaceAll=true.");

        var newContent = replaceAll
            ? content.Replace(oldString, newString, StringComparison.Ordinal)
            : ReplaceSingle(content, oldString, newString, mode);

        if (dryRun)
            return ToolResult.Success(UnifiedDiff.Compute(content, newContent, filePath));

        var ok = await ssh.WriteFileAsync(filePath, newContent, ct).ConfigureAwait(false);
        if (!ok)
            return ToolResult.Error($"Error writing ssh:{filePath}");
        var successMessage = replaceAll
            ? $"File updated: ssh:{filePath} ({count} {(count == 1 ? "occurrence" : "occurrences")} replaced)"
            : $"File updated: ssh:{filePath}";
        return ToolResult.Success(successMessage);
    }

    /// <summary>单锚点编辑：按 mode 定位唯一锚点构造新内容（replace / insert_after / insert_before）。</summary>
    private static string ReplaceSingle(string content, string oldString, string newString, string mode)
    {
        var idx = content.IndexOf(oldString, StringComparison.Ordinal);
        return mode switch
        {
            "insert_after" => string.Concat(content.AsSpan(0, idx + oldString.Length), newString, content.AsSpan(idx + oldString.Length)),
            "insert_before" => string.Concat(content.AsSpan(0, idx), newString, content.AsSpan(idx)),
            _ => string.Concat(content.AsSpan(0, idx), newString, content.AsSpan(idx + oldString.Length)),
        };
    }

    private static int CountOccurrences(string text, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return 0;
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }

    private string BuildMissingFileMessage(string fullPath, string workingDirectory)
    {
        var message = $"Error: File not found: {fullPath}";
        var suggestion = FindSuggestedPath(fullPath, workingDirectory);

        if (!string.IsNullOrEmpty(suggestion))
            message += $" Did you mean: {suggestion}?";

        return message;
    }

    private string? FindSuggestedPath(string fullPath, string workingDirectory)
    {
        var sameDirectorySuggestion = FindSameDirectorySuggestion(fullPath);
        if (!string.IsNullOrEmpty(sameDirectorySuggestion))
            return sameDirectorySuggestion;

        try
        {
            var root = Directory.Exists(workingDirectory)
                ? workingDirectory
                : Path.GetDirectoryName(fullPath);

            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return null;

            var requestedName = Path.GetFileName(fullPath);
            var requestedRelativePath = GetRelativeDisplayPath(root, fullPath);
            var bestPath = default(string);
            var bestScore = int.MaxValue;
            var candidatesChecked = 0;
            const int maxCandidates = 500;

            foreach (var candidate in EnumerateFilesSafely(root))
            {
                if (candidatesChecked++ >= maxCandidates)
                    break;

                var candidateName = Path.GetFileName(candidate);
                var nameDistance = Core.Text.StringDistance.LevenshteinIgnoreCase(requestedName, candidateName);
                var relativeDistance = Core.Text.StringDistance.LevenshteinIgnoreCase(
                    requestedRelativePath,
                    GetRelativeDisplayPath(root, candidate));
                var score = Math.Min(nameDistance, relativeDistance);

                if (score >= bestScore)
                    continue;

                bestScore = score;
                bestPath = candidate;
            }

            if (bestPath == null)
                return null;

            var threshold = Math.Max(3, requestedName.Length / 3);
            return bestScore <= threshold ? GetRelativeDisplayPath(root, bestPath) : null;
        }
        catch (Exception ex)
        {
            // 有意吞掉：路径建议（"Did you mean"）是尽力而为的提示路径，
            // 任何 IO/权限异常都不应阻断主错误信息的返回——无建议即可。
            _logger?.LogDebug(ex, "Path suggestion search failed for {Path}", fullPath);
            return null;
        }
    }

    private string? FindSameDirectorySuggestion(string fullPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return null;

            var requestedBaseName = Path.GetFileNameWithoutExtension(fullPath);
            foreach (var candidate in Directory.GetFiles(directory))
            {
                if (string.Equals(candidate, fullPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.Equals(
                    Path.GetFileNameWithoutExtension(candidate),
                    requestedBaseName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetFileName(candidate);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            // 有意吞掉：同目录建议失败仅意味着不追加提示，不影响主流程。
            _logger?.LogDebug(ex, "Same-directory path suggestion failed for {Path}", fullPath);
            return null;
        }
    }

    private static IEnumerable<string> EnumerateFilesSafely(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] files;
            string[] childDirectories;
            try
            {
                files = Directory.GetFiles(current);
                childDirectories = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is DirectoryNotFoundException || ex is IOException)
            {
                continue;
            }

            foreach (var file in files)
                yield return file;

            foreach (var child in childDirectories)
                pending.Push(child);
        }
    }

    private static string GetRelativeDisplayPath(string root, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(root, path);
            return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
        }
        catch (Exception ex)
        {
            // 有意吞掉：相对路径仅用于展示，失败时退回原始路径即可。
            System.Diagnostics.Debug.WriteLine($"GetRelativeDisplayPath failed: {ex.Message}");
            return path;
        }
    }
}
