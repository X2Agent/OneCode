using System.ComponentModel;
using System.Text;
using OneCode.App.Services.Cache;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Remote;

namespace OneCode.App.Tools;

/// <summary>
/// Reads a file from the local filesystem with optional offset/limit and line numbers.
/// Supports binary detection, file cache dedup, and large-file streaming.
/// </summary>
public sealed class ReadTool
{
    private const int MaxResultSizeChars = 20_000;
    private const int BinaryProbeBytes = 512;
    private const int LargeFileLineThreshold = 400;
    private const int DefaultReadLimit = 400;

    private readonly IFileContentCache _cache;
    private readonly IWorkingDirectoryAccessor _wd;
    private readonly SshRemoteService _ssh;

    public ReadTool(IFileContentCache cache, IWorkingDirectoryAccessor wd, SshRemoteService ssh)
        => (_cache, _wd, _ssh) = (cache, wd, ssh);

    [Description("Read a text file from the local filesystem, returning content with line numbers. " +
                 "Supports offset/limit pagination for large files and streaming for files exceeding 400 lines. " +
                 "Binary files (detected by NUL byte probe of the first 512 bytes) are rejected with a suggestion to use a binary tool. " +
                 "Path safety: must resolve within the working directory or AdditionalWorkingDirectories; symlink escape attacks are blocked. " +
                 "Sensitive credential files (~/.ssh/id_rsa, ~/.aws/credentials, .env, ~/.kube/config) are hard-blocked by FileSystemInvariant. " +
                 "Cache: when FileContentCache is configured, repeated reads of unchanged content return a dedup stub to save tokens. " +
                 "Output is truncated at 20,000 chars; large files emit a 'use offset and limit' hint. " +
                 "SSH: when an SSH remote is connected, reads are performed remotely.")]
    public async Task<ToolResult> ReadAsync(
        [Description("Absolute or relative path of the file to read. Relative paths resolve against the working directory.")] string filePath,
        [Description("Line number to start reading from (1-based). Default 1. Use with limit to paginate large files.")] int offset = 1,
        [Description("Maximum number of lines to return. Default 400, hard max 2000. Increase to read more context, decrease to save tokens.")] int limit = DefaultReadLimit,
        CancellationToken ct = default)
    {
        offset = Math.Max(1, offset);
        limit = Math.Max(1, limit);

        try
        {
            if (_ssh is { IsConnected: true } ssh)
                return await ReadRemoteAsync(ssh, filePath, offset, limit, ct).ConfigureAwait(false);

            var resolveResult = PathsHelper.SafeResolve(filePath, _wd.WorkingDirectory, _wd.AdditionalDirectories);
            if (!resolveResult.IsSuccess)
                return ToolResult.Error($"Error: {resolveResult.Error}");
            var fullPath = resolveResult.Value;

            if (_cache != null && _cache.TryDedupRead(fullPath, offset, limit))
            {
                return ToolResult.Success(FileContentCache.FileUnchangedStub);
            }

            if (!File.Exists(fullPath))
                return ToolResult.Error($"Error: File not found: {fullPath}");

            if (await IsBinaryFileAsync(fullPath, ct))
            {
                return ToolResult.Error($"Error: Cannot read binary file: {fullPath}. Use a tool intended for binary or image content instead.");
            }

            var (selectedLines, totalLineCount) = await ReadLinesStreamingAsync(fullPath, offset, limit, ct);

            var content = FormatWithLineNumbers(selectedLines, offset);

            if (content.Length > MaxResultSizeChars)
            {
                var truncated = content[..MaxResultSizeChars];
                var lastNewline = truncated.LastIndexOf('\n');
                content = (lastNewline > 0 ? truncated[..lastNewline] : truncated)
                          + $"\n[Output truncated at {MaxResultSizeChars} chars]";
            }

            if (_cache != null)
                _cache.SetAfterRead(fullPath, content, offset, limit);

            return ToolResult.Success(FormatResult(fullPath, offset, selectedLines.Length, totalLineCount, content, limit));
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Error reading {filePath}: {ex.Message}");
        }
    }

    private async Task<ToolResult> ReadRemoteAsync(SshRemoteService ssh, string filePath, int offset, int limit, CancellationToken ct)
    {
        var content = await ssh.ReadFileAsync(filePath, ct).ConfigureAwait(false);
        if (content is null)
            return ToolResult.Error($"Error: File not found or unreadable over SSH: {filePath}");

        var lines = content.Replace("\r\n", "\n").Split('\n');
        var total = lines.Length;
        var selected = lines.Skip(Math.Max(0, offset - 1)).Take(limit).ToArray();
        var formatted = FormatWithLineNumbers(selected, offset);
        return ToolResult.Success(FormatResult($"ssh:{filePath}", offset, selected.Length, total, formatted, limit));
    }

    /// <summary>
    /// Format lines with padded line numbers: "  1→ content".
    /// </summary>
    private static string FormatWithLineNumbers(string[] lines, int startLineNumber)
    {
        if (lines.Length == 0) return "";

        var maxLineNo = startLineNumber + lines.Length - 1;
        var width = maxLineNo.ToString(CultureInfo.InvariantCulture).Length;

        var sb = new StringBuilder(lines.Length * 80);
        for (int i = 0; i < lines.Length; i++)
        {
            var lineNo = (startLineNumber + i).ToString(CultureInfo.InvariantCulture).PadLeft(width);
            sb.Append(lineNo);
            sb.Append("→ ");
            sb.AppendLine(lines[i]);
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    private static string FormatResult(string path, int startLine, int readCount, int totalLines, string content, int requestedLimit)
    {
        var endLine = readCount > 0 ? startLine + readCount - 1 : startLine;
        var largeFileNotice = totalLines > LargeFileLineThreshold
            ? $"\nNote: file has {totalLines} lines. Use offset and limit to read smaller ranges."
            : "";
        var rangeNotice = requestedLimit != int.MaxValue && startLine + readCount - 1 < totalLines
            ? "\nNote: additional lines were omitted. Increase limit or adjust offset to read more."
            : "";

        return $"""
                File: {path}
                Lines {startLine}-{endLine} of {totalLines}:{largeFileNotice}{rangeNotice}
                {content}
                """;
    }

    private static async Task<bool> IsBinaryFileAsync(string fullPath, CancellationToken ct)
    {
        var buffer = new byte[BinaryProbeBytes];
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: BinaryProbeBytes,
            useAsync: true);

        var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
        for (var i = 0; i < read; i++)
        {
            if (buffer[i] == 0)
                return true;
        }

        return false;
    }

    private static async Task<(string[] Lines, int TotalCount)> ReadLinesStreamingAsync(
        string fullPath, int offset, int limit, CancellationToken ct)
    {
        var start = Math.Max(0, offset - 1);
        var selected = new List<string>(Math.Min(limit, 1000));
        var totalLineCount = 0;
        var collected = 0;

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 8192,
            useAsync: true);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            totalLineCount++;

            if (totalLineCount - 1 < start)
                continue;

            if (collected >= limit)
                continue;

            selected.Add(line);
            collected++;
        }

        return (selected.ToArray(), totalLineCount);
    }
}
