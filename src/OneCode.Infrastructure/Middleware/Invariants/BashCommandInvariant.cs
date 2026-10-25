using OneCode.Core.Domain;

namespace OneCode.Infrastructure.Middleware.Invariants;

/// <summary>
/// Bash 命令安全不变量：硬禁止高危命令模式（rm -rf /、fork bomb、curl|sh 等）。
/// Layer 0——BypassPermissions 也生效。
///
/// 模式字符串的规范来源为 <see cref="OneCode.Core.Permissions.DangerousCommandPatterns.Layer0HardDeny"/>。
/// 本类在静态初始化时从该列表构建 <see cref="Regex"/> 对象，确保 Pipeline 层（本类）和
/// Executor 层（MAF ShellPolicy，通过 <see cref="DenyPatternStrings"/>）使用完全相同的模式，
/// 消除历史 GeneratedRegex 与字符串常量分别声明导致的漂移风险。
///
/// 注意：使用非 Compiled 正则以保持 AOT 友好。Layer 0 检查每次 Bash 工具调用一次，
/// 13 个正则的解释匹配成本可忽略。
/// </summary>
public sealed class BashCommandInvariant : ISafetyInvariant
{
    /// <summary>Shell 类工具名称。</summary>
    private static readonly HashSet<string> ShellTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bash", "Shell", "BackgroundRun",
    };

    /// <summary>
    /// 危险命令模式列表——从 <see cref="OneCode.Core.Permissions.DangerousCommandPatterns.Layer0HardDeny"/>
    /// 单一事实源构建。修改模式只需更新 <c>DangerousCommandPatterns</c>，本类自动同步。
    /// </summary>
    private static readonly (string Name, Regex Regex)[] DangerousPatterns =
        OneCode.Core.Permissions.DangerousCommandPatterns.Layer0HardDeny
            .Select(p => (p.Name, new Regex(
                p.Pattern,
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(2))))
            .ToArray();

    /// <summary>
    /// 同步校验单条 Shell 命令是否命中 Layer 0 黑名单。供 <see cref="CheckAsync"/> 与
    /// 直接工具入口（如 <c>BackgroundRunTool</c>）共用，消除重复正则定义。
    /// </summary>
    public static InvariantCheckResult CheckCommand(string command)
    {
        foreach (var (name, regex) in DangerousPatterns)
        {
            try
            {
                if (regex.IsMatch(command))
                {
                    return InvariantCheckResult.Deny(
                        $"[SAFETY] Dangerous command pattern detected: '{name}'. Command blocked.");
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return InvariantCheckResult.Deny(
                    "[SAFETY] Command pattern check timed out (possible ReDoS). Command blocked.");
            }
        }

        return InvariantCheckResult.Allow;
    }

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

        return new(CheckCommand(command));
    }

    private static string? ExtractCommand(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("command", out var cmdObj) && cmdObj is string cmd)
            return cmd;
        if (parameters.TryGetValue("cmd", out var aliasObj) && aliasObj is string aliasCmd)
            return aliasCmd;
        return null;
    }

    /// <summary>
    /// 将本不变量的危险模式导出为正则字符串列表，供 MAF <see cref="Microsoft.Agents.AI.Tools.Shell.ShellPolicy"/>
    /// 纵深防御使用。Pipeline 层（本类）和 Executor 层（ShellPolicy）双重拦截，
    /// 即使有代码路径绕过 Pipeline 直接调用 executor 也能兜底。
    /// 模式来源：<see cref="OneCode.Core.Permissions.DangerousCommandPatterns.Layer0HardDeny"/>。
    /// </summary>
    public static IReadOnlyList<string> DenyPatternStrings { get; } =
        OneCode.Core.Permissions.DangerousCommandPatterns.Layer0HardDeny
            .Select(p => p.Pattern)
            .ToList()
            .AsReadOnly();
}
