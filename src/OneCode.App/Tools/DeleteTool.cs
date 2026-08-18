using System.ComponentModel;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Remote;

namespace OneCode.App.Tools;

/// <summary>
/// Deletes files or directories from the local filesystem.
///
/// Semantics:
///  - Files are deleted directly; empty directories too
///  - Non-empty directories require recursive=true (count of direct children is reported otherwise)
///  - dryRun=true returns a preview (path, type, size / entry count) without touching disk
///  - v1 is local-only: when an SSH remote is connected the call fails explicitly
///
/// Safety:
///  - Path safety: must resolve within the working directory or AdditionalWorkingDirectories
///  - Layer-0: FileSystemInvariant treats FileDelete-category tools as write operations —
///    sensitive paths (.git/, ~/.ssh/, .env, credentials) and symlink chains are hard-blocked
///  - Not part of FileEditContract or EditTransaction snapshots: deletion is irreversible
///    and always requires explicit approval (ToolRisk.Destructive)
/// </summary>
public sealed class DeleteTool
{
    private readonly IWorkingDirectoryAccessor _wd;
    private readonly SshRemoteService _ssh;

    public DeleteTool(IWorkingDirectoryAccessor wd, SshRemoteService ssh)
        => (_wd, _ssh) = (wd, ssh);

    [Description("Delete a file or directory. Files and empty directories are deleted directly; " +
                 "non-empty directories require recursive=true. " +
                 "dryRun=true returns a preview (type, size, entry count) without deleting — use it to verify scope first. " +
                 "Path safety: must resolve within the working directory; .git/ and sensitive paths are hard-blocked by FileSystemInvariant. " +
                 "Deletion is irreversible — prefer moving files to a trash directory or relying on git when recoverability matters. " +
                 "SSH remote: not supported over SSH; deletion is local-only.")]
    public Task<ToolResult> DeleteAsync(
        [Description("Absolute or relative path of the file or directory to delete. Relative paths resolve against the working directory.")] string filePath,
        [Description("When true, delete directories recursively including all contents. Required for non-empty directories.")] bool recursive = false,
        [Description("When true, return a preview of what would be deleted without touching disk. Use to verify the scope before deleting.")] bool dryRun = false,
        CancellationToken ct = default)
    {
        if (_ssh is { IsConnected: true })
            return Task.FromResult(ToolResult.Error(
                "Error: Delete is not supported while an SSH remote is connected (local filesystem only)."));

        var resolveResult = PathsHelper.SafeResolve(filePath, _wd.WorkingDirectory, _wd.AdditionalDirectories);
        if (!resolveResult.IsSuccess)
            return Task.FromResult(ToolResult.Error($"Error: {resolveResult.Error}"));
        var fullPath = resolveResult.Value!;

        // Workspace roots (working directory + additional directories) are never deletable —
        // neither SafeResolve nor FileSystemInvariant rejects them, and wiping the project
        // root is never a legitimate single-tool action.
        if (IsWorkspaceRoot(fullPath))
            return Task.FromResult(ToolResult.Error(
                "Error: Refusing to delete a workspace root (the working directory or an additional directory). " +
                "Delete specific files or subdirectories instead."));

        try
        {
            if (File.Exists(fullPath))
            {
                var size = new FileInfo(fullPath).Length;
                if (dryRun)
                    return Task.FromResult(ToolResult.Success(
                        $"[Dry run] Would delete file: {fullPath} ({size} bytes)"));

                File.Delete(fullPath);
                return Task.FromResult(ToolResult.Success($"File deleted: {fullPath}"));
            }

            if (Directory.Exists(fullPath))
            {
                var entries = Directory.GetFileSystemEntries(fullPath);
                if (entries.Length > 0 && !recursive)
                    return Task.FromResult(ToolResult.Error(
                        $"Error: Directory is not empty ({entries.Length} direct entries): {fullPath}. " +
                        "Pass recursive=true to delete it with all contents."));

                if (dryRun)
                {
                    var fileCount = CountFiles(fullPath);
                    return Task.FromResult(ToolResult.Success(
                        $"[Dry run] Would delete directory recursively: {fullPath} " +
                        $"({entries.Length} direct entries, {fileCount} files in total)"));
                }

                Directory.Delete(fullPath, recursive: true);
                return Task.FromResult(ToolResult.Success(
                    $"Directory deleted: {fullPath} ({entries.Length} direct entries)"));
            }

            return Task.FromResult(ToolResult.Error($"Error: File or directory not found: {fullPath}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(ToolResult.Error($"Error deleting {filePath}: {ex.Message}"));
        }
    }

    private bool IsWorkspaceRoot(string fullPath)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(fullPath, _wd.WorkingDirectory, comparison))
            return true;
        if (_wd.AdditionalDirectories is { } dirs)
        {
            foreach (var dir in dirs)
            {
                if (string.Equals(fullPath, dir, comparison))
                    return true;
            }
        }
        return false;
    }

    private static int CountFiles(string directory)
    {
        var count = 0;
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                if (Directory.Exists(entry))
                    pending.Push(entry);
                else
                    count++;
            }
        }
        return count;
    }
}
