using Microsoft.Agents.AI.Tools.Shell;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Remote;

namespace OneCode.App.Tools;

/// <summary>
/// Shared helper for shell-based tool execution (Bash, PowerShell).
/// Provides path validation, SSH execution, and output formatting utilities.
/// </summary>
public static class ShellExecutionHelper
{
    public const int DefaultTimeoutMs = 120_000;
    public const int MaxTimeoutMs = 600_000;
    public const int MaxOutputChars = 100_000;

    public static bool CanValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return !path.Contains('*')
            && !path.Contains('?')
            && !path.Contains('$')
            && !path.Contains("$(", StringComparison.Ordinal)
            && !path.Contains('`');
    }

    public static string? ValidateReferencedPaths(
        string command,
        string workingDirectory,
        Func<string, IEnumerable<string>> extractPaths,
        IEnumerable<string>? additionalDirs = null)
    {
        foreach (var path in extractPaths(command))
        {
            if (!CanValidatePath(path))
                continue;

            var resolved = PathsHelper.SafeResolve(path, workingDirectory, additionalDirs);
            if (!resolved.IsSuccess)
                return $"Error: command references path outside the working directory: {path}. Use /add-dir <path> to grant access.";
        }

        return null;
    }

    public static string BuildOutput(string stdout, string stderr) =>
        string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}\n[stderr]\n{stderr}";

    public static string FormatResult(string command, int exitCode, string output) =>
        $"Command: {command}\nExit code: {exitCode}\nOutput:\n{output.TrimEnd()}";

    /// <summary>
    /// Converts a <see cref="ShellResult"/> + optional warning into a <see cref="ToolResult"/>.
    /// Shared by BashTool and PowerShellTool for both local and SSH execution paths.
    /// </summary>
    public static ToolResult ToToolResult(ShellResult shellResult, string command, string? warning = null)
    {
        var output = BuildOutput(shellResult.Stdout, shellResult.Stderr);
        if (!string.IsNullOrWhiteSpace(warning))
            output = $"[warning] {warning}\n{output}";
        if (shellResult.Truncated)
            output += "\n[Output truncated using head/tail strategy]";

        var formatted = FormatResult(command, shellResult.ExitCode, output);
        return shellResult.ExitCode == 0
            ? ToolResult.Success(formatted)
            : ToolResult.Error(formatted, "Fix the command and retry.");
    }

    /// <summary>
    /// Executes a shell command via SSH using <see cref="SshShellExecutor"/>.
    /// Shared by BashTool and PowerShellTool — eliminates the duplicated
    /// <c>ExecuteViaSshAsync</c> private method from both tools.
    /// </summary>
    public static async Task<ToolResult> ExecuteViaSshAsync(
        SshRemoteService ssh, string command, int timeoutSeconds, CancellationToken ct)
    {
        var timeoutMs = ClampTimeoutMs(timeoutSeconds);
        await using var executor = new SshShellExecutor(ssh, new SshShellExecutorOptions
        {
            Timeout = TimeSpan.FromMilliseconds(timeoutMs),
            MaxOutputBytes = MaxOutputChars,
        });
        var shellResult = await executor.RunAsync(command, ct).ConfigureAwait(false);

        var output = BuildOutput(shellResult.Stdout, shellResult.Stderr);
        if (string.IsNullOrWhiteSpace(output))
            output = $"[ssh] Command exited with code {shellResult.ExitCode}";
        if (shellResult.Truncated)
            output += "\n[Output truncated using head/tail strategy]";

        var formatted = FormatResult(command, shellResult.ExitCode, output);
        return shellResult.ExitCode == 0
            ? ToolResult.Success(formatted)
            : ToolResult.Error(formatted, "Fix the command and retry.");
    }

    public static int ClampTimeoutMs(int timeoutSeconds) =>
        Math.Clamp(timeoutSeconds * 1000, 1000, MaxTimeoutMs);
}
