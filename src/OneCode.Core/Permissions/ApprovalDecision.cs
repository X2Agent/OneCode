namespace OneCode.Core.Permissions;

/// <summary>
/// 用户对工具调用审批的决策结果。
/// 事件驱动审批中由 TUI 通过 TaskCompletionSource 回传给 Main/Team 路径。
/// </summary>
public enum ApprovalDecision
{
    /// <summary>本次允许，下次仍询问。</summary>
    AllowOnce,

    /// <summary>永久允许（跨会话持久化）。</summary>
    AllowAlways,

    /// <summary>
    /// 本次对话全部允许。当前调用照常批准；同一 run 内的后续审批由 ApprovalBroker
    /// 自动放行（run 的权限模式在管道装配时已固化），跨 run 的会话级放行由
    /// PermissionModeProvider 运行时覆盖切换到 BypassPermissions 实现（不持久化）。
    /// Team 工作流桥不提供该档位（成员策略固定为 PermissionMode.Team）。
    /// </summary>
    AllowAllConversation,

    /// <summary>拒绝。</summary>
    Deny,
}
