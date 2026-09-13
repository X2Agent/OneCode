namespace OneCode.Core.Workflows;

/// <summary>Durable lifecycle of a MAF workflow run.</summary>
public enum WorkflowRunState
{
    Active,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// 四模式 durable run 的终结原因共享词汇（自 <c>Core/Build</c> 提升；
/// GoalRun 终结原因已在复用）。持久化枚举值顺序不可变更（兼容红线：JSON 数字编码）。
/// </summary>
public enum RunTerminalReason
{
    /// <summary>Agent 正常完成且最终验证通过（或无文件变更）。</summary>
    Completed,

    /// <summary>Agent 未完成即到达轮数上限。</summary>
    TurnLimitReached,

    /// <summary>Agent 未完成即预算耗尽。</summary>
    BudgetExceeded,

    /// <summary>用户取消（ESC 或取消令牌）。</summary>
    Cancelled,

    /// <summary>最终验证（build/test）失败，事务已回滚。</summary>
    ValidationFailed,

    /// <summary>Agent 管道抛出非取消异常。</summary>
    AgentException,

    /// <summary>必需权限被拒绝且无法化解。</summary>
    PermissionRefused,

    /// <summary>等待必要的澄清或范围确认。</summary>
    ClarificationRequired,

    /// <summary>被外部依赖或工作区冲突阻断。</summary>
    Blocked,
}

/// <summary>
/// <see cref="RunTerminalReason"/> 与各模式持久化状态词汇的映射单源。
/// Goal/Team 的终态 reason 投影由本类承担，替代散落在流式装配层的内联 switch。
/// </summary>
public static class RunTerminalReasonMap
{
    /// <summary>GoalRunState → 终结原因（原 OrchestrationStreamService 内联映射）。</summary>
    public static RunTerminalReason FromGoalState(OneCode.Core.Goals.GoalRunState state) => state switch
    {
        OneCode.Core.Goals.GoalRunState.Completed => RunTerminalReason.Completed,
        OneCode.Core.Goals.GoalRunState.Paused => RunTerminalReason.BudgetExceeded,
        OneCode.Core.Goals.GoalRunState.Cancelled => RunTerminalReason.Cancelled,
        OneCode.Core.Goals.GoalRunState.Blocked => RunTerminalReason.Blocked,
        OneCode.Core.Goals.GoalRunState.Failed => RunTerminalReason.ValidationFailed,
        _ => RunTerminalReason.AgentException,
    };

    /// <summary>
    /// TeamRunStatus → 终结原因（原 OrchestrationStreamService 内联映射）。
    /// <paramref name="hasError"/> 对应非终态但携带错误的状态（原 <c>{ Error: not null }</c> 分支，
    /// 优先级位于 Failed/RolledBack 之后、兜底 Completed 之前）。
    /// </summary>
    public static RunTerminalReason FromTeamStatus(OneCode.Core.Coordinator.TeamRunStatus status, bool hasError = false) => status switch
    {
        OneCode.Core.Coordinator.TeamRunStatus.Succeeded => RunTerminalReason.Completed,
        OneCode.Core.Coordinator.TeamRunStatus.Cancelled => RunTerminalReason.Cancelled,
        OneCode.Core.Coordinator.TeamRunStatus.Blocked => RunTerminalReason.Blocked,
        OneCode.Core.Coordinator.TeamRunStatus.RolledBack or OneCode.Core.Coordinator.TeamRunStatus.Failed
            => RunTerminalReason.ValidationFailed,
        _ when hasError => RunTerminalReason.AgentException,
        _ => RunTerminalReason.Completed,
    };
}

/// <summary>Stable inputs required to create or resume a workflow run.</summary>
public sealed record WorkflowRunRegistration(
    string RunId,
    string RunKind,
    string DefinitionHash);

/// <summary>Durable identity and routing metadata for a pending MAF request.</summary>
public sealed record WorkflowPendingRequest(
    string RequestId,
    string PortId,
    string CommandId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt = null);

/// <summary>
/// Durable workflow runtime metadata. Business stores remain authoritative for product state;
/// this record owns only runtime identity, fencing and the last reconciled checkpoint.
/// </summary>
public sealed record WorkflowRunRecord(
    string RunId,
    string RunKind,
    string DefinitionHash,
    long FencingToken,
    WorkflowRunState State,
    string? CheckpointId,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt = null,
    WorkflowPendingRequest? PendingRequest = null,
    int ExecutionGeneration = 0)
{
    public bool IsTerminal => State is WorkflowRunState.Completed
        or WorkflowRunState.Failed
        or WorkflowRunState.Cancelled;
}
