using OneCode.Core.Build;
using OneCode.Core.Errors;
using OneCode.Core.Permissions;
using OneCode.Core.PlanMode;

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

    /// <summary>
    /// Team 任务状态变更快照（开始时 Status=null，结束时为终态名称），
    /// 携带全量计数（已完成/总数/活跃/阻塞），供 TUI 进度面板消费。Team 专有。
    /// </summary>
    public sealed record TeamTaskProgress(
        string TaskId,
        string TaskTitle,
        string AssigneeRole,
        string? Status,
        int CompletedTasks,
        int TotalTasks,
        int ActiveTasks = 0,
        int BlockedTasks = 0) : OrchestrationEvent;

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

    /// <summary>用户在澄清/审批交互中给出的回答回显，供 TUI 写入会话记录。</summary>
    public sealed record TeamUserResponse(string TeamName, string Response) : OrchestrationEvent;

    /// <summary>
    /// Plan 聚合投影变更（事件通道统一）：计划提交/审批/执行阶段推进时
    /// 由 <c>PlanCardPublisher</c> 经统一领域事件总线发射。Workflow 为唯一权威载荷，
    /// 计划卡片投影（标题/步骤/阶段/文档路径）由 TUI 宿主从聚合与 revision store 异步解析。
    /// </summary>
    public sealed record PlanProjectionChanged(PlanWorkflow Workflow) : OrchestrationEvent;

    /// <summary>
    /// Build 聚合状态投影变更（事件通道统一）：受控 Build attempt 的
    /// durable 状态变化时由 <c>ControlledBuildAttemptContext.BuildStateEventFactory</c> 发射，
    /// 流式管道（StreamingSession）解信封为流内 BuildRunStateEvent（QueryEvent 契约不变）。
    /// </summary>
    public sealed record BuildStateProjectionChanged(BuildRun Run) : OrchestrationEvent;

    /// <summary>
    /// Agent 待办清单快照（事件通道统一）：Harness <c>TodoProvider</c> 的每会话状态
    /// 在每次 agent run 结束时由 <c>TodoProjectionService</c> 经统一领域事件总线发射。
    /// 权威源是 provider 的 <c>GetAllTodosAsync</c> 全量快照，不是工具调用增量重建
    /// （增量重建要自行复刻 add/complete/remove 的幂等语义，漏事件即漂移）。
    /// 空列表 = 当前会话没有待办，消费方应隐藏面板。
    /// </summary>
    public sealed record TodoProjectionChanged(IReadOnlyList<TodoListItem> Items) : OrchestrationEvent;
}

/// <summary>
/// 待办清单条目投影——与 MAF <c>TodoItem</c> 解耦：Core 不引用 MAF 类型，
/// TUI 只需要展示字段。
/// </summary>
public sealed record TodoListItem(int Id, string Title, string? Description, bool IsComplete);
