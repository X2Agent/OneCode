using System.ComponentModel;
using Microsoft.Agents.AI.Tools.Shell;
using OneCode.App.Session;
using OneCode.Infrastructure.Middleware.Invariants;
using OneCode.Infrastructure.Remote;

namespace OneCode.App.Tools;

/// <summary>
/// Executes shell commands via MAF <see cref="ShellExecutor"/> (LocalShellExecutor or SshShellExecutor).
/// Delegates execution to MAF; retains OneCode-specific pre-execution validation and soft warnings.
/// </summary>
/// <remarks>
/// 可选依赖（保留可空，缺失时走 fallback 路径）：
/// - <see cref="_ssh"/>：SSH 远程执行；仅在配置远程连接时非空
/// - <see cref="_shellExecutorManager"/>：会话级持久 shell；缺失时 fallback 到 Stateless 模式
/// - <see cref="_sessionManager"/>：会话隔离；缺失时 <c>_shellExecutorManager</c> 路径不启用
/// </remarks>
public sealed class BashTool
{
    private static readonly string[] SedDangerousPatterns =
    [
        "s/.*/",
        "s/\\/\\/.*/",
        "/^/d",
        "/./d",
        "s/^.*$//",
        "s|.*||",
        "d}"
    ];

    private readonly IWorkingDirectoryAccessor _wd;
    private readonly SshRemoteService _ssh;
    private readonly ConversationShellExecutorManager _shellExecutorManager;
    private readonly ISessionConversationAccess _sessionManager;

    public BashTool(
        IWorkingDirectoryAccessor wd,
        SshRemoteService ssh,
        ConversationShellExecutorManager shellExecutorManager,
        ISessionConversationAccess sessionManager)
        => (_wd, _ssh, _shellExecutorManager, _sessionManager)
            = (wd, ssh, shellExecutorManager, sessionManager);

    [Description("Execute a shell command. Platform behavior: on Windows invokes pwsh (PowerShell Core) or powershell.exe; on Unix invokes /bin/bash. " +
                 "Use this tool for cross-platform shell commands when you do NOT need PowerShell-specific syntax. " +
                 "For cmdlets like Get-ChildItem, Invoke-WebRequest, or $PSItem pipelines, prefer the PowerShellTool. " +
                 "Safety: dangerous patterns (rm -rf /, git push --force, curl|sh, etc.) are hard-blocked by BashCommandInvariant; " +
                 "destructive commands emit a [warning] prefix; sed -i without backup suffix is rejected. " +
                 "Persistence: when a session-scoped shell executor is configured, commands run in a persistent shell preserving cwd/env across calls; otherwise a fresh process is spawned per call. " +
                 "Output is truncated at 100,000 chars using head/tail strategy; timeouts kill the entire process tree.")]
    public async Task<ToolResult> ExecuteAsync(
        [Description("The shell command to execute. Use Unix-style syntax on Linux/macOS and PowerShell-compatible syntax on Windows. " +
                     "Multi-line scripts are supported; chain with && or ; as needed.")] string command,
        [Description("A brief one-line description of what the command does, for audit logging. Not shown to the user.")] string? description = null,
        [Description("Timeout in seconds. Default 120, max 600. On timeout the process tree is killed and partial stdout/stderr is returned.")] int timeout = 120,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Error("Error: command cannot be empty");

        if (SshToolHelper.IsActive(_ssh))
            return await ShellExecutionHelper.ExecuteViaSshAsync(_ssh!, command, timeout, ct).ConfigureAwait(false);

        var workingDirectory = _wd.WorkingDirectory;
        if (!Directory.Exists(workingDirectory))
            return ToolResult.Error($"Error: working directory not found: {workingDirectory}");

        var pathValidationError = ShellExecutionHelper.ValidateReferencedPaths(
            command, workingDirectory, BashCommandClassifier.ExtractReferencedPaths, _wd.AdditionalDirectories);
        if (pathValidationError != null)
            return ToolResult.Error(pathValidationError);

        var sedError = ValidateSedCommand(command);
        if (sedError != null)
            return ToolResult.Error(sedError);

        var warning = BashCommandClassifier.GetDestructiveCommandWarning(command);

        ShellResult shellResult;
        try
        {
            shellResult = await ExecuteShellAsync(workingDirectory, command, timeout, ct).ConfigureAwait(false);
        }
        catch (ShellCommandRejectedException ex)
        {
            return ToolResult.Error(ShellExecutionHelper.FormatResult(command, -1, $"Error: {ex.Message}"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolResult.Error(ShellExecutionHelper.FormatResult(command, -1,
                $"[Timed out after {timeout}s]"));
        }

        return ShellExecutionHelper.ToToolResult(shellResult, command, warning);
    }

    private async Task<ShellResult> ExecuteShellAsync(string workingDirectory, string command, int timeout, CancellationToken ct)
    {
        var timeoutMs = ShellExecutionHelper.ClampTimeoutMs(timeout);

        if (_shellExecutorManager is not null
            && _sessionManager?.ForegroundConversation is { } conversation)
        {
            return await _shellExecutorManager.ExecuteAsync(
                conversation.Id, workingDirectory, command,
                TimeSpan.FromMilliseconds(timeoutMs), ct).ConfigureAwait(false);
        }

        await using var executor = new LocalShellExecutor(new LocalShellExecutorOptions
        {
            Mode = ShellMode.Stateless,
            WorkingDirectory = workingDirectory,
            MaxOutputBytes = ShellExecutionHelper.MaxOutputChars,
            Timeout = TimeSpan.FromMilliseconds(timeoutMs),
            AcknowledgeUnsafe = true,
            Policy = new ShellPolicy(denyList: BashCommandInvariant.DenyPatternStrings),
        });
        return await executor.RunAsync(command, ct).ConfigureAwait(false);
    }

    private static string? ValidateSedCommand(string command)
    {
        if (!command.Contains("sed", StringComparison.OrdinalIgnoreCase))
            return null;

        var normalized = command.Replace(" ", "").Replace("\t", "");

        var hasInPlace = normalized.Contains("-i") && !normalized.Contains("-i.");
        var hasDangerous = Array.Exists(SedDangerousPatterns,
            p => normalized.Contains(p, StringComparison.Ordinal));

        if (hasDangerous)
            return "Error: sed command contains a potentially destructive pattern. " +
                   "If intentional, break the task into smaller steps with explicit file backups first.";

        if (hasInPlace)
            return "Warning: sed -i without backup suffix is destructive. " +
                   "Use -i.bak to create a backup, or set the pattern more carefully.";

        return null;
    }

    public static bool IsSedDangerous(string command) =>
        command.Contains("sed", StringComparison.OrdinalIgnoreCase) &&
        ValidateSedCommand(command) != null;
}
