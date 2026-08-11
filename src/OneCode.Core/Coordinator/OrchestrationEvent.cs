using OneCode.Core.Errors;
using OneCode.Core.Permissions;

namespace OneCode.Core.Coordinator;

/// <summary>
/// 编排模式（Team/Goal 等）执行过程中推送给 TUI 层的事件。
/// 这是一个 Core 层的抽象类型，避免 Core 依赖 App 层的 TuiEvent。
/// App 层负责将 OrchestrationEvent 转换为具体的 TuiEvent。
///
/// PR-17 评估：展平为独立 sealed record 列表需改动 20+ 调用点（TuiEventMapper、
/// OrchestrationStreamService、TeamWorkflowRunner 等），风险/收益比不足——暂保留嵌套 record 层次。
///
/// 事件分两类：
/// - 通用事件（ToolStart/ToolDone/TextDelta/Error/ApprovalRequest）：任何编排模式都可能产生，可被 Goal 等模式复用。
/// - Team 专有事件（AgentCoordination/AgentMessage）：仅 Team 模式产生。
/// </summary>
public abstract record OrchestrationEvent
{
    /// <summary>Agent 间协调消息（orchestrator → researcher 等）。Team 专有。</summary>
    public sealed record AgentCoordination(string FromName, string? FromColor, string ToName, string? ToColor, string? Content) : OrchestrationEvent;

    /// <summary>单个 Agent 的消息输出。Team 专有。</summary>
    public sealed record AgentMessage(string AgentName, string? AgentColor, string Content) : OrchestrationEvent;

    /// <summary>工具调用开始。通用事件。</summary>
    public sealed record ToolStart(string AgentName, string ToolId, string Name, string? ToolInput = null) : OrchestrationEvent;

    /// <summary>工具调用完成。通用事件。ToolId 用于与 ToolStart 精确匹配。</summary>
    public sealed record ToolDone(string AgentName, string Name, bool IsError, string? Result = null, string? ToolInput = null, string ToolId = "") : OrchestrationEvent;

    /// <summary>文本增量。通用事件。</summary>
    public sealed record TextDelta(string AgentName, string Text) : OrchestrationEvent;

    /// <summary>Team/Goal 文件变更事件。由共享编辑中间件在每次 Write/Edit 后发出。</summary>
    public sealed record FileChanged(
        string AgentName,
        string FileName,
        IReadOnlyList<string> AddedLines,
        IReadOnlyList<string> RemovedLines) : OrchestrationEvent;

    /// <summary>错误。通用事件。Problem 携带结构化错误详情（ERR-1.5）。</summary>
    public sealed record Error(string Message, AgentProblemDetails? Problem = null) : OrchestrationEvent;

    /// <summary>
    /// 事件驱动审批请求 — Team 子 Agent 的工具审批通过此事件推送到 TUI。
    /// 持有 Core <see cref="Permissions.ApprovalRequest"/> 作为唯一权威载荷；
    /// <see cref="ResponseSource"/> 是编排层回传通道（展示层投影由 TuiEventMapper 生成）。
    /// </summary>
    public sealed record ApprovalRequest(Permissions.ApprovalRequest Request) : OrchestrationEvent
    {
        /// <summary>用于回传用户审批决策的 TaskCompletionSource。</summary>
        public TaskCompletionSource<ApprovalDecision> ResponseSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>TeamRun 需求澄清请求。存在阻断问题时不得进入计划审批或创建写事务。</summary>
    public sealed record TeamClarificationRequest(
        TeamRunId RunId,
        string TeamName,
        string Goal,
        IReadOnlyList<string> Questions) : OrchestrationEvent;

    /// <summary>
    /// TeamRun 计划级审批通知事件。审批决策通过 MAF RequestPort 持久化，
    /// 不再通过 TaskCompletionSource 阻塞。此事件仅用于通知 TUI 展示审批卡片。
    /// </summary>
    public sealed record TeamPlanApprovalRequest(
        TeamRunId RunId,
        string TeamName,
        string PlanSummary,
        IReadOnlyList<string> Tasks,
        IReadOnlyList<string> RequiredGates) : OrchestrationEvent;
}
