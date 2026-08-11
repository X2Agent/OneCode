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

    /// <summary>拒绝。</summary>
    Deny,
}
