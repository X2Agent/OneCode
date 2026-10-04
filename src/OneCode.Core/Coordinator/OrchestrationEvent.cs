using OneCode.Core.Build;
using OneCode.Core.Errors;
using OneCode.Core.PlanMode;

namespace OneCode.Core.Coordinator;

/// <summary>
/// 编排模式（Goal 等多步工作流）执行过程中推送给 TUI 层的事件。
/// 这是一个 Core 层的抽象类型，避免 Core 依赖 App 层的 TuiEvent。
/// App 层负责将 OrchestrationEvent 转换为具体的 TuiEvent。
///
/// 展平为独立 sealed record 列表需改动 20+ 调用点（TuiEventMapper、
/// OrchestrationStreamService 等），风险/收益比不足，保留嵌套 record 层次。
/// </summary>
public abstract record OrchestrationEvent
{
    /// <summary>单个 Agent 的消息输出。子代理 DAG / Goal 工作流产生。</summary>
    public sealed record AgentMessage(string AgentName, string? AgentColor, string Content) : OrchestrationEvent;

    /// <summary>工具调用开始。通用事件。</summary>
    public sealed record ToolStart(string AgentName, string ToolId, string Name, string? ToolInput = null) : OrchestrationEvent;

    /// <summary>工具调用完成。通用事件。ToolId 用于与 ToolStart 精确匹配。</summary>
    public sealed record ToolDone(string AgentName, string Name, bool IsError, string? Result = null, string? ToolInput = null, string ToolId = "") : OrchestrationEvent;

    /// <summary>文本增量。通用事件。</summary>
    public sealed record TextDelta(string AgentName, string Text) : OrchestrationEvent;

    /// <summary>文件变更事件。由共享编辑中间件在每次 Write/Edit 后发出。</summary>
    public sealed record FileChanged(
        string AgentName,
        string FileName,
        IReadOnlyList<string> AddedLines,
        IReadOnlyList<string> RemovedLines) : OrchestrationEvent;

    /// <summary>错误。通用事件。Problem 携带结构化错误详情。</summary>
    public sealed record Error(string Message, AgentProblemDetails? Problem = null) : OrchestrationEvent;

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
