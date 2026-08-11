namespace OneCode.Core.Permissions;

/// <summary>
/// 权限模式——控制工具调用的审批策略
/// </summary>
public enum PermissionMode
{
    Default,
    Plan,
    Auto,
    AcceptEdits,
    BypassPermissions,
    DontAsk,
    Bubble,
    /// <summary>
    /// GOAL 模式专用：自主执行不中断，但有安全边界。
    /// 只读工具 + 文件写入工具（路径校验后）+ 只读 Shell 自动放行；
    /// 危险 Shell 命令直接 Deny（强制 Agent 改方案）；
    /// 其他工具路径校验后 Allow（保持自主性，不弹窗）。
    /// </summary>
    GoalAuto,
    /// <summary>
    /// TEAM 模式专用：多 Agent 协作模式。
    /// 与 AcceptEdits 类似（文件写入 + 常规开发命令自动放行），
    /// 但危险 Shell 命令通过 OrchestrationEvent.ApprovalRequest 事件驱动审批。
    /// 子 Agent 无 ToolApprovalAgent（InProcessExecution 无审批响应循环）。
    /// </summary>
    Team,
}

/// <summary>
/// 权限决策原因——记录为什么做出了某个权限决策。
/// 用于审计、调试和用户理解权限行为。
/// </summary>
public abstract record PermissionDecisionReason
{
    public sealed record Other(string Reason) : PermissionDecisionReason;
    public sealed record BubbleRequest(string ToolName, string? Input) : PermissionDecisionReason;
}

/// <summary>
/// 权限规则
/// </summary>
public sealed record PermissionRule(
    string ToolName,
    string? InputPattern = null);

/// <summary>
/// 权限规则分组（按来源）
/// </summary>
public sealed record PermissionRuleGroup(
    IReadOnlyList<PermissionRule>? AlwaysAllow = null,
    IReadOnlyList<PermissionRule>? AlwaysDeny = null,
    IReadOnlyList<PermissionRule>? AlwaysAsk = null);

/// <summary>
/// 工具权限上下文——完整的权限检查上下文
/// </summary>
public sealed record ToolPermissionContext
{
    public PermissionMode Mode { get; init; }
    /// <summary>The active session's working directory used for path traversal checks.</summary>
    public string WorkingDirectory { get; init; } = Environment.CurrentDirectory;
    public IReadOnlyDictionary<string, AdditionalWorkingDirectory> AdditionalWorkingDirectories { get; init; }
        = new Dictionary<string, AdditionalWorkingDirectory>();
    public IReadOnlyDictionary<string, PermissionRuleGroup> RulesBySource { get; init; }
        = new Dictionary<string, PermissionRuleGroup>();
    public HashSet<string> SessionAllowlist { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 额外工作目录（来源和路径）
/// </summary>
public sealed record AdditionalWorkingDirectory(
    string Path,
    WorkingDirectorySource Source);

public enum WorkingDirectorySource
{
    CliArg,
    AddDirCommand,
    Worktree,
    Config,
}

/// <summary>
/// 权限检查结果——包含决策原因和消息。
///
/// 支持 4 种行为：Allow、Deny、Ask、Passthrough
/// </summary>
public sealed record PermissionCheckResult
{
    public required PermissionDecision Decision { get; init; }
    public PermissionDecisionReason? DecisionReason { get; init; }
    public string? Message { get; init; }

    public static PermissionCheckResult Allow => new() { Decision = PermissionDecision.Allow };
    public static PermissionCheckResult Deny(string reason) => new() { Decision = PermissionDecision.Deny, Message = reason, DecisionReason = new PermissionDecisionReason.Other(reason) };
    public static PermissionCheckResult Ask(string message) => new() { Decision = PermissionDecision.Ask, Message = message };
    public static PermissionCheckResult Passthrough(string message) => new() { Decision = PermissionDecision.Passthrough, Message = message };
}

public enum PermissionDecision
{
    Allow,
    Deny,
    Ask,
    Passthrough,
}

/// <summary>
/// 权限检查器接口
/// </summary>
public interface IPermissionChecker
{
    Task<PermissionCheckResult> CheckAsync(
        string toolName,
        JsonElement toolInput,
        ToolPermissionContext context,
        CancellationToken ct = default);
}

/// <summary>
/// 权限规则解析器
/// </summary>
public static class PermissionRuleParser
{
    public static PermissionRule? Parse(string ruleString)
    {
        if (string.IsNullOrWhiteSpace(ruleString)) return null;

        var parenIdx = ruleString.IndexOf('(');
        if (parenIdx < 0)
            return new PermissionRule(ruleString.Trim());

        var toolName = ruleString[..parenIdx].Trim();
        var pattern = ruleString[(parenIdx + 1)..].TrimEnd(')').Trim();
        return new PermissionRule(toolName, pattern.Length == 0 ? null : pattern);
    }

    public static bool GlobMatch(string pattern, string input)
    {
        if (string.IsNullOrEmpty(pattern)) return true;
        if (pattern == "*" || pattern == "**") return true;

        if (pattern.EndsWith(" *", StringComparison.Ordinal))
        {
            var prefix = pattern[..^2];
            return input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.StartsWith("*", StringComparison.Ordinal) && pattern.EndsWith("*", StringComparison.Ordinal))
        {
            var middle = pattern[1..^1];
            return input.Contains(middle, StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.StartsWith("*", StringComparison.Ordinal))
        {
            var suffix = pattern[1..];
            return input.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.EndsWith("*", StringComparison.Ordinal))
        {
            var prefix = pattern[..^1];
            return input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(pattern, input, StringComparison.OrdinalIgnoreCase);
    }
}
