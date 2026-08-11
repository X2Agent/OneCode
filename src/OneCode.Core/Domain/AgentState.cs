namespace OneCode.Core.Domain;

/// <summary>
/// Agent 任务阶段状态。
///
/// 只保留运行时有实际转移和闸门逻辑的 3 个状态。
/// Plan 模式的只读约束由权限层（PlanModePermissionStrategy）负责，
/// 不在状态机中重复实现。
/// </summary>
public enum AgentState
{
    /// <summary>正常执行（所有工具可用）。</summary>
    Active,

    /// <summary>错误恢复（连续失败，3-strike 升级到 Blocked）。</summary>
    Recovering,

    /// <summary>等待用户干预（停止执行）。</summary>
    Blocked,
}
