using System.ComponentModel;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Remote;
using OneCode.Infrastructure.Text;

namespace OneCode.App.Tools;

/// <summary>
/// Writes file contents to disk, mirroring the TypeScript Write tool.
///
/// When overwriting an existing file:
///  - Preserves the file's original BOM/encoding (UTF-8 BOM, UTF-16 LE/BE)
///  - Preserves the file's original line-ending style (CRLF / LF) by normalising
///    the incoming content to match before writing
///  - Notifies the LSP server after a successful write so diagnostics stay fresh
///  - Validates path safety (via PathsHelper.SafeResolve) and content size
///
/// Supports <c>dryRun: true</c> — returns a unified diff preview without writing.
/// </summary>
public sealed class WriteTool
{
    private const int MaxContentLength = 10_000_000; // 10 MB

    private readonly ILspNotifier _notifier;
    private readonly IWorkingDirectoryAccessor _wd;
    private readonly SshRemoteService _ssh;

    public WriteTool(ILspNotifier notifier, IWorkingDirectoryAccessor wd, SshRemoteService ssh)
    {
        _notifier = notifier;
        _wd = wd;
        _ssh = ssh;
    }

    [Description("Write content to a file, creating it if it does not exist or fully overwriting it if it does. " +
                 "Prefer the Edit tool for targeted modifications to existing files — Write replaces the entire file content. " +
                 "Encoding preservation: when overwriting, the file's original BOM/encoding (UTF-8 BOM, UTF-16 LE/BE) and line-ending style (CRLF/LF) are detected and preserved. " +
                 "Path safety: must resolve within the working directory; .git/ and sensitive paths (~/.ssh/, ~/.aws/credentials, .env) are hard-blocked by FileSystemInvariant. " +
                 "Validation: content is rejected if empty or exceeding 10MB. " +
                 "LSP integration: after a successful write, the LSP server is notified and a diagnostics summary is returned. " +
                 "dryRun=true returns a unified diff preview without touching disk. " +
                 "SSH: when an SSH remote is connected, writes are performed remotely.")]
    public async Task<ToolResult> WriteAsync(
        [Description("Absolute or relative path of the file to write. Relative paths resolve against the working directory. Parent directories are created automatically.")] string filePath,
        [Description("The full content to write to the file. Existing content is replaced entirely; use Edit for partial modifications.")] string content,
        [Description("When true, return a unified diff preview (old -> new) without writing to disk. Use to preview changes before committing.")] bool dryRun = false,
        CancellationToken ct = default)
    {
        // Content size guard — prevents OOM and excessive token use.
        // Path safety is handled by PathsHelper.SafeResolve below.
        if (string.IsNullOrEmpty(content))
            return ToolResult.Error("Error: content is empty");
        if (content.Length > MaxContentLength)
            return ToolResult.Error($"Error: content exceeds maximum size ({MaxContentLength / 1024 / 1024}MB, got {content.Length / 1024 / 1024}MB)");

        try
        {
            if (_ssh is { IsConnected: true } ssh)
                return await WriteRemoteAsync(ssh, filePath, content, dryRun, ct).ConfigureAwait(false);

            var resolved = PathsHelper.SafeResolve(filePath, _wd.WorkingDirectory, _wd.AdditionalDirectories);
            if (!resolved.IsSuccess)
                return ToolResult.Error($"Error: {resolved.Error}");
            var fullPath = resolved.Value!;

            var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false) as System.Text.Encoding;
            var lineEndingStyle = FileEncodingHelper.LineEndingStyle.Lf;
            var existingContent = string.Empty;

            if (File.Exists(fullPath))
            {
                var fileSize = new FileInfo(fullPath).Length;
                if (fileSize > PathsHelper.MaxFileReadSize)
                    return ToolResult.Error($"Error: File is too large to process ({fileSize / 1024 / 1024}MB). " +
                           $"Maximum supported size is {PathsHelper.MaxFileReadSize / 1024 / 1024}MB.");

                (existingContent, encoding, lineEndingStyle) =
                    await FileEncodingHelper.ReadWithEncodingAsync(fullPath, ct);
            }

            var finalContent = FileEncodingHelper.NormalizeLineEndings(content, lineEndingStyle);

            if (dryRun)
            {
                var diff = UnifiedDiff.Compute(existingContent, finalContent, filePath);
                return ToolResult.Success(diff);
            }

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            await FileEncodingHelper.WriteWithEncodingAsync(fullPath, finalContent, encoding, ct);

            var message = await FileWritePipeline.CompleteWriteAsync(
                fullPath, finalContent, _notifier,
                $"Successfully wrote {finalContent.Length} characters to {fullPath}", ct).ConfigureAwait(false);
            return ToolResult.Success(message);
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Error writing {filePath}: {ex.Message}");
        }
    }

    private async Task<ToolResult> WriteRemoteAsync(SshRemoteService ssh, string filePath, string content, bool dryRun, CancellationToken ct)
    {
        var existing = await ssh.ReadFileAsync(filePath, ct).ConfigureAwait(false) ?? "";
        if (dryRun)
            return ToolResult.Success(UnifiedDiff.Compute(existing, content, filePath));

        var ok = await ssh.WriteFileAsync(filePath, content, ct).ConfigureAwait(false);
        return ok
            ? ToolResult.Success($"Successfully wrote {content.Length} characters to ssh:{filePath}")
            : ToolResult.Error($"Error writing ssh:{filePath}");
    }
}
