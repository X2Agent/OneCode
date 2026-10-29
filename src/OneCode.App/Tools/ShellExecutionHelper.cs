using OneCode.Core.Exec;
using OneCode.Infrastructure;

namespace OneCode.App.Tools;

/// <summary>Shared helper for shell-based tool execution (Bash, PowerShell).</summary>
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
            && !path.Contains('`')
            && !path.StartsWith("@(", StringComparison.Ordinal)
            && !path.StartsWith('{');
    }

    /// <summary>
    /// 展开风险判定：token 内含 <c>$</c> 或反引号（变量/命令替换），静态校验无法约束其
    /// 解析目标——任何模式（含 GoalAuto）下都拒绝执行，要求改写为显式字面路径或人工确认。
    /// </summary>
    private static bool HasExpansionRisk(string path)
        => path.Contains('$')
            || path.Contains('`')
            || path.StartsWith("@(", StringComparison.Ordinal)
            || path.StartsWith('{');

    public static string? ValidateReferencedPaths(
        string command,
        string workingDirectory,
        Func<string, IEnumerable<string>> extractPaths,
        IEnumerable<string>? additionalDirs = null)
    {
        foreach (var path in extractPaths(command))
        {
            if (!CanValidatePath(path))
            {
                // 展开风险操作数无法证明解析目标仍在授权目录内，必须在执行前拒绝。
                if (HasExpansionRisk(path))
                {
                    return $"Error: command operand '{path}' uses a variable, command expansion, or script expression, so its target cannot be verified against the workspace boundary. " +
                           "Use a literal path inside the workspace (variables and expanded expressions are not allowed), or ask the user to run it manually.";
                }

                // 纯通配操作数：校验首个通配符之前的静态前缀（如 src/*.cs → src），
                // 保留工作区内 glob 的正常用法，仍能拦截 ../*/etc 形式的越界。
                var wildcardIndex = path.IndexOfAny(['*', '?']);
                var staticPrefix = wildcardIndex > 0 ? path[..wildcardIndex] : string.Empty;
                if (wildcardIndex <= 0 || string.IsNullOrWhiteSpace(staticPrefix))
                    continue;

                var prefixResolved = PathsHelper.SafeResolve(staticPrefix, workingDirectory, additionalDirs);
                if (!prefixResolved.IsSuccess)
                    return $"Error: command references path outside the working directory: {path}. Use /add-dir <path> to grant access.";
                continue;
            }

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

    /// <summary>Converts a shell execution result + optional warning into a <see cref="ToolResult"/>.</summary>
    public static ToolResult ToToolResult(ShellExecutionResult shellResult, string command, string? warning = null)
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

    public static int ClampTimeoutMs(int timeoutSeconds) =>
        Math.Clamp(timeoutSeconds * 1000, 1000, MaxTimeoutMs);
}
