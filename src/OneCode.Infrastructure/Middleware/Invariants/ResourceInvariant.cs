using OneCode.Core.Domain;

namespace OneCode.Infrastructure.Middleware.Invariants;

/// <summary>
/// 资源安全不变量：检测无限循环命令和无 timeout 的长运行命令。
/// Layer 0——BypassPermissions 也生效。
/// </summary>
public sealed partial class ResourceInvariant : ISafetyInvariant
{
    /// <summary>Shell 类工具名称。</summary>
    private static readonly HashSet<string> ShellTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bash", "PowerShell", "Shell",
    };

    public ValueTask<InvariantCheckResult> CheckAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct)
    {
        if (!ShellTools.Contains(toolName))
            return new(InvariantCheckResult.Allow);

        var command = ExtractCommand(parameters);
        if (command is null)
            return new(InvariantCheckResult.Allow);

        // 无限循环检测
        foreach (var pattern in InfiniteLoopPatterns)
        {
            if (pattern.IsMatch(command))
            {
                return new(InvariantCheckResult.Deny(
                    $"[SAFETY] Potential infinite loop detected: '{pattern.Name}'. Command blocked."));
            }
        }

        return new(InvariantCheckResult.Allow);
    }

    private static string? ExtractCommand(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("command", out var cmdObj) && cmdObj is string cmd)
            return cmd;
        return null;
    }

    /// <summary>无限循环检测模式。</summary>
    private static readonly DangerousLoopPattern[] InfiniteLoopPatterns =
    [
        // while true; do ... done 无 sleep/break
        new("WhileTrueNoBreak", WhileTrueNoBreakRegex()),
        // for ((;;)) 无限 for 循环
        new("InfiniteFor", InfiniteForRegex()),
        // tail -f 无 timeout（长时间挂起）
        new("TailFollow", TailFollowRegex()),
        // yes 命令（无限输出）
        new("YesCommand", YesCommandRegex()),
    ];

    private sealed record DangerousLoopPattern(string Name, Regex Regex)
    {
        public bool IsMatch(string input) => Regex.IsMatch(input);
    }

    [GeneratedRegex(@"while\s+true\b(?:(?!sleep|break).)*done", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex WhileTrueNoBreakRegex();

    [GeneratedRegex(@"for\s*\(\s*;\s*;\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex InfiniteForRegex();

    [GeneratedRegex(@"\btail\s+.*-f\b", RegexOptions.IgnoreCase)]
    private static partial Regex TailFollowRegex();

    [GeneratedRegex(@"^\s*yes\b", RegexOptions.IgnoreCase)]
    private static partial Regex YesCommandRegex();
}
