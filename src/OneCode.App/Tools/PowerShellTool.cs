using System.ComponentModel;
using Microsoft.Agents.AI.Tools.Shell;
using OneCode.Infrastructure.Abstractions;
using OneCode.Infrastructure.Middleware.Invariants;
using OneCode.Infrastructure.Remote;

namespace OneCode.App.Tools;

/// <summary>
/// Executes PowerShell commands via MAF <see cref="LocalShellExecutor"/> (Stateless mode).
/// Delegates execution to MAF; retains OneCode-specific pre-execution validation and soft warnings.
/// </summary>
public sealed class PowerShellTool
{
    private readonly IProcessRunner _processRunner;
    private readonly IWorkingDirectoryAccessor _wd;
    private readonly SshRemoteService _ssh;

    /// <summary>
    /// Combined deny patterns for PowerShell executor-level defense.
    /// Includes Layer0 cross-shell patterns PLUS PowerShell-specific dangerous cmdlets.
    /// </summary>
    private static readonly IReadOnlyList<string> PowerShellDenyPatterns =
        BashCommandInvariant.DenyPatternStrings
            .Concat(new[]
            {
                @"\bSet-ExecutionPolicy\b",
                @"\b(Format-Volume|Clear-Disk)\b",
                @"\b(Restart-Computer|Stop-Computer)\b",
                @"\bStart-Process\b[^|;&\n]*-(Verb|v)(:|\s+)RunAs\b",
            })
            .ToList()
            .AsReadOnly();

    public PowerShellTool(IProcessRunner processRunner, IWorkingDirectoryAccessor wd, SshRemoteService ssh)
        => (_processRunner, _wd, _ssh) = (processRunner, wd, ssh);

    [Description("Execute a PowerShell command or script. Uses pwsh (PowerShell Core) when available, otherwise powershell.exe on Windows. " +
                 "On Unix requires pwsh to be installed. Prefer this over BashTool when you need PowerShell-specific syntax: cmdlets (Get-ChildItem, Invoke-WebRequest, Select-String), " +
                 "the $PSItem/$_ pipeline variable, Object[] collections, or Windows-only modules (e.g. ActiveDirectory, Hyper-V). " +
                 "Safety: destructive command patterns emit a [warning] prefix; dangerous patterns are hard-blocked by the PowerShellCommandInvariant. " +
                 "Output is truncated at 100,000 chars; timeouts kill the entire process tree. " +
                 "Note: this tool always spawns a fresh process per call (no persistent shell); use BashTool if you need cwd/env persistence across calls.")]
    public async Task<ToolResult> ExecuteAsync(
        [Description("The PowerShell command or script to execute. Multiple statements can be separated by ; or newlines. " +
                     "Use $ErrorActionPreference='Stop' to fail-fast on non-terminating errors.")] string command,
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
            command, workingDirectory, PowerShellCommandClassifier.ExtractReferencedPaths, _wd.AdditionalDirectories);
        if (pathValidationError != null)
            return ToolResult.Error(pathValidationError);

        var warning = PowerShellCommandClassifier.GetDestructiveCommandWarning(command);

        var hasPwsh = await _processRunner.CommandExistsAsync("pwsh").ConfigureAwait(false);
        if (!hasPwsh && !OperatingSystem.IsWindows())
            return ToolResult.Error("Error: PowerShell (pwsh/powershell) not found. Please install PowerShell.");

        var shell = hasPwsh ? "pwsh" : "powershell";
        var timeoutMs = ShellExecutionHelper.ClampTimeoutMs(timeout);

        ShellResult shellResult;
        try
        {
            await using var executor = new LocalShellExecutor(new LocalShellExecutorOptions
            {
                Mode = ShellMode.Stateless,
                Shell = shell,
                WorkingDirectory = workingDirectory,
                MaxOutputBytes = ShellExecutionHelper.MaxOutputChars,
                Timeout = TimeSpan.FromMilliseconds(timeoutMs),
                AcknowledgeUnsafe = true,
                Policy = new ShellPolicy(denyList: PowerShellDenyPatterns),
            });
            shellResult = await executor.RunAsync(command, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return ToolResult.Error("Error: PowerShell (pwsh/powershell) not found. Please install PowerShell.");
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
}
