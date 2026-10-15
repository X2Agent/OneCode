using System.ComponentModel;
using Microsoft.Agents.AI.Tools.Shell;
using OneCode.Core.Exec;
using OneCode.App.Session;

namespace OneCode.App.Tools;

/// <summary>
/// Executes shell commands through the configured <see cref="IShellExecutor"/> Provider.
/// Retains OneCode-specific pre-execution validation and soft warnings.
/// 通过 shell 参数支持 bash（默认）与 powershell 两种方言，原独立 PowerShellTool 已并入本工具。
/// </summary>
/// <remarks>
/// 执行后端由 <see cref="IShellExecutor"/> 统一承接；会话范围通过请求传递给 Provider。
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
    private readonly IShellExecutor _shellExecutor;
    private readonly ISessionConversationAccess _sessionManager;

    public BashTool(
        IWorkingDirectoryAccessor wd,
        IShellExecutor shellExecutor,
        ISessionConversationAccess sessionManager)
        => (_wd, _shellExecutor, _sessionManager)
            = (wd, shellExecutor, sessionManager);

    /// <summary>
    /// 构建平台感知的工具描述：Windows 下明确持久会话运行 PowerShell（pwsh），避免模型误发 Unix 命令（如 <c>date +...</c>）。
    /// </summary>
    public static string BuildDescription()
    {
        var platformHeader = OperatingSystem.IsWindows()
            ? "Execute a shell command. Operating system: Windows. Default persistent shell: PowerShell (pwsh). " +
              "Use PowerShell syntax — NOT bash/sh (e.g., Get-Date -Format 'yyyy-MM-dd_HH-mm-ss' instead of date +..., " +
              "$env:VAR instead of $VAR, Get-ChildItem instead of ls -la). "
            : "Execute a shell command. Operating system: Unix/Linux/macOS. Default persistent shell: bash/sh. ";

        return platformHeader +
               "Prefer the dedicated Write and Edit tools to create or modify files instead of shell redirection or echo. " +
               "Dialect selection: shell=\"bash\" (default) runs in the persistent session shell (pwsh on Windows, bash on Unix) " +
               "preserving cwd and environment across calls; shell=\"powershell\" spawns an isolated fresh PowerShell process per call. " +
               "Safety: dangerous patterns (rm -rf /, git push --force, curl|sh, Set-ExecutionPolicy, Format-Volume, etc.) are hard-blocked; " +
               "destructive commands emit a [warning] prefix; sed -i without backup suffix is rejected (bash path). " +
               "Output is truncated at 100,000 chars using head/tail strategy; timeouts kill the entire process tree.";
    }

    [Description("Execute a shell command. On Windows the default persistent shell is PowerShell (pwsh) — use PowerShell syntax " +
                 "(e.g., Get-Date -Format 'yyyy-MM-dd_HH-mm-ss' instead of Unix date +...). On Unix the default persistent shell is bash. " +
                 "Prefer the dedicated Write/Edit tools to create or modify files instead of shell redirection. " +
                 "Dialect selection: shell=\"bash\" (default) runs in the persistent session shell (pwsh on Windows, bash on Unix) " +
                 "preserving cwd/env across calls; shell=\"powershell\" spawns an isolated fresh PowerShell process per call. " +
                 "Safety: dangerous patterns (rm -rf /, git push --force, curl|sh, etc.) are hard-blocked; shell=\"powershell\" " +
                 "additionally hard-blocks Set-ExecutionPolicy / Format-Volume / Restart-Computer / elevated Start-Process; " +
                 "destructive commands emit a [warning] prefix; sed -i without backup suffix is rejected (bash path). " +
                 "Output is truncated at 100,000 chars using head/tail strategy; timeouts kill the entire process tree.")]
    public async Task<ToolResult> ExecuteAsync(
        [Description("The shell command to execute. On Windows, use PowerShell syntax (e.g., Get-Date -Format 'yyyy-MM-dd_HH-mm-ss', $env:VAR); " +
                     "on Linux/macOS, use bash/sh syntax. Prefer Write/Edit tools for file creation and edits.")] string command,
        [Description("Shell execution mode: \"bash\" (default — runs in the persistent session shell: pwsh on Windows, bash on Unix) " +
                     "or \"powershell\" (spawns an isolated fresh PowerShell process with additional PowerShell cmdlet guards).")] string shell = "bash",
        [Description("A brief one-line description of what the command does, for audit logging. Not shown to the user.")] string? description = null,
        [Description("Timeout in seconds. Default 120, max 600. On timeout the process tree is killed and partial stdout/stderr is returned.")] int timeout = 120,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Error("Error: command cannot be empty");

        var isPowerShell = shell?.Equals("powershell", StringComparison.OrdinalIgnoreCase) ?? false;
        if (!isPowerShell && !(shell?.Equals("bash", StringComparison.OrdinalIgnoreCase) ?? true))
            return ToolResult.Error($"Error: unsupported shell '{shell}'. Use \"bash\" (default) or \"powershell\".");

        var workingDirectory = _wd.WorkingDirectory;
        if (!Directory.Exists(workingDirectory))
            return ToolResult.Error($"Error: working directory not found: {workingDirectory}");

        var pathValidationError = ShellExecutionHelper.ValidateReferencedPaths(
            command, workingDirectory,
            isPowerShell ? PowerShellCommandClassifier.ExtractReferencedPaths : BashCommandClassifier.ExtractReferencedPaths,
            _wd.AdditionalDirectories);
        if (pathValidationError is not null)
            return ToolResult.Error(pathValidationError);

        var sedError = isPowerShell ? null : ValidateSedCommand(command);
        if (sedError is not null)
            return ToolResult.Error(sedError);

        var warning = isPowerShell
            ? PowerShellCommandClassifier.GetDestructiveCommandWarning(command)
            : BashCommandClassifier.GetDestructiveCommandWarning(command);

        try
        {
            var shellResult = await _shellExecutor.ExecuteAsync(
                new ShellExecutionRequest(
                    command,
                    workingDirectory,
                    isPowerShell ? "powershell" : "bash",
                    timeout,
                    _sessionManager.ForegroundConversation?.Id),
                ct).ConfigureAwait(false);
            return ShellExecutionHelper.ToToolResult(shellResult, command, warning);
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
        ValidateSedCommand(command) is not null;
}
