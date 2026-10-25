namespace OneCode.App.Query;

/// <summary>Base type for all query-layer events consumed by QueryStreamEngine / TUI adapter.</summary>
public abstract record QueryEvent;

/// <summary>流式文本增量 — 来自 API 的 text_delta。</summary>
public sealed record TextDeltaEvent(string Text) : QueryEvent;

/// <summary>Thinking 增量 — 来自 API 的 thinking_delta。</summary>
public sealed record ThinkingDeltaEvent(string Text) : QueryEvent;

/// <summary>模型随最终回答一并生成的下一步建议。</summary>
public sealed record SuggestionsEvent(IReadOnlyList<string> Items) : QueryEvent;

/// <summary>工具调用开始 — 含工具名称、唯一 ID 和操作目标（参数摘要）。
/// AgentName 为多代理工作流中执行该工具的代理名，主代理为 null。</summary>
public sealed record ToolStartEvent(
    string ToolId,
    string ToolName,
    string? ToolInput = null,
    string? AgentName = null) : QueryEvent;

/// <summary>工具调用完成 — 含结果/错误和原始输入。
/// AgentName 为多代理工作流中执行该工具的代理名，主代理为 null。</summary>
public sealed record ToolDoneEvent(
    string ToolId,
    string ToolName,
    bool IsError,
    string? Result = null,
    string? ToolInput = null,
    string? AgentName = null) : QueryEvent;

/// <summary>Persisted BuildRun state projection. DoneEvent remains stream completion only.</summary>
public sealed record BuildRunStateEvent(
    OneCode.Core.Build.BuildRunId RunId,
    OneCode.Core.Build.BuildRunState State,
    long SequenceNumber,
    int CompletedTasks = 0,
    int TotalTasks = 0,
    OneCode.Core.Workflows.RunTerminalReason? TerminalReason = null,
    string? FailureSummary = null,
    OneCode.Core.Build.BuildScopeSnapshot? Scope = null,
    OneCode.Core.Build.BuildValidationStatus? ValidationStatus = null,
    int ChangedFiles = 0,
    int TurnsCompleted = 0,
    int ActiveTasks = 0,
    int BlockedTasks = 0) : QueryEvent
{
    public static BuildRunStateEvent From(OneCode.Core.Build.BuildRun run) => new(
        run.Id,
        run.State,
        run.SequenceNumber,
        run.Plan?.Tasks.Count(task => task.Status == OneCode.Core.Build.BuildTaskStatus.Completed) ?? 0,
        run.Plan?.Tasks.Count ?? 0,
        run.TerminalReason,
        run.FailureSummary,
        run.Scope,
        run.Validations.LastOrDefault()?.Status,
        run.ChangedFiles.Count,
        run.Metrics.TurnsCompleted,
        run.Plan?.Tasks.Count(task => task.Status == OneCode.Core.Build.BuildTaskStatus.InProgress) ?? 0,
        run.Plan?.Tasks.Count(task => task.Status == OneCode.Core.Build.BuildTaskStatus.Pending
            && task.DependsOn.Count > 0) ?? 0);
}

/// <summary>Structured Build delivery manifest emitted after all business gates close.</summary>
public sealed record BuildRunCompletedEvent(OneCode.Core.Build.BuildRunResult Result) : QueryEvent;

/// <summary>查询完成 — 含 token 统计和终止原因。</summary>
public sealed record DoneEvent(
    string? FullText,
    TokenUsage? Usage,
    int TurnsCompleted,
    OneCode.Core.Workflows.RunTerminalReason TerminalReason,
    SessionId? SessionId = null,
    bool TransactionRolledBack = false,
    string? ValidationFailureSummary = null) : QueryEvent;

/// <summary>轮次开始 — 每轮 API 查询开始前触发。</summary>
public sealed record TurnStartedEvent(int TurnNumber) : QueryEvent;

/// <summary>轮次结束。</summary>
public sealed record TurnCompletedEvent(int TurnNumber) : QueryEvent;

/// <summary>API/网络错误 — 不中断对话循环，可由 TUI 显示后继续等待用户输入。</summary>
public sealed record ErrorEvent(string Message, bool Recoverable = true) : QueryEvent;

/// <summary>面向用户的系统级提示（非助手输出、非错误）——如 Plan 纠偏轮自动追加的说明。</summary>
public sealed record NoticeEvent(string Message) : QueryEvent;

/// <summary>
/// 预算预警（全模式通用）：可计费消耗（超出基线）跨越 70%（Early/黄）或 90%（Late/橙）
/// 阈值时由 BudgetGuard 经事件流发布，TUI 渲染预警横幅。按级别去重递升，不重复刷屏。
/// </summary>
public sealed record BudgetWarningEvent(
    OneCode.Core.Goals.GoalBudgetWarningLevel Level,
    long UsedTokens,
    long LimitTokens) : QueryEvent;

/// <summary>子代理实时进度（fork 工具行 live 文案与状态栏 agents 徽标的数据源）。</summary>
public sealed record SubagentProgressEvent(
    string ForkId,
    string AgentName,
    SubagentProgressPhase Phase,
    int ToolCallCount = 0,
    int TurnCount = 0,
    string? CurrentToolName = null,
    long ElapsedMs = 0) : QueryEvent;

// 审批不再经事件流：请求由 IApprovalPromptSink 端口下发，事件契约保持纯数据。

/// <summary>上下文压缩建议 — token 使用率首次跨越 70% 告警阈值时触发。</summary>
public sealed record CompactSuggestedEvent(string Message) : QueryEvent;

/// <summary>模式进度状态（BUILD / PLAN / GOAL / LOOP 共用）。</summary>
public enum ModeProgressState
{
    /// <summary>正在执行。</summary>
    Running,
    /// <summary>等待用户或外部条件。</summary>
    Waiting,
    /// <summary>已正常完成。</summary>
    Completed,
    /// <summary>失败收尾。</summary>
    Failed,
    /// <summary>已暂停，可恢复。</summary>
    Paused,
}

/// <summary>模式进度投影 — 主对话只渲染当前用户可理解的阶段。</summary>
public sealed record ModeProgressEvent(
    WorkingMode Mode,
    string Message,
    ModeProgressState State = ModeProgressState.Running,
    int? Completed = null,
    int? Total = null) : QueryEvent;

/// <summary>Goal 分解完成 — 子目标编号清单，执行前让用户看到计划。</summary>
public sealed record GoalPlanEvent(IReadOnlyList<string> Steps) : QueryEvent;

/// <summary>Goal 结构化子目标进度快照 — 驱动子目标清单展示。</summary>
public sealed record GoalPlanUpdatedEvent(
    IReadOnlyList<OneCode.Core.Goals.GoalStepSnapshot> Steps,
    IReadOnlyList<OneCode.Core.Goals.GoalStepExecutionEvidence> Executions,
    int CurrentIndex,
    OneCode.Core.Goals.GoalBudgetUsage? Usage = null,
    OneCode.Core.Goals.GoalBudget? Budget = null) : QueryEvent;

/// <summary>Goal 的结构化执行结果，供验证、恢复与渲染读取。</summary>
public sealed record GoalResultEvent(
    int Completed,
    int Failed,
    int Skipped,
    int Total,
    bool Committed,
    IReadOnlyList<string> CompletedGoals,
    IReadOnlyList<string> FailedGoals,
    IReadOnlyList<string> SkippedGoals,
    string ValidationSummary,
    IReadOnlyList<string>? ChangedFiles = null,
    string? FinalConclusion = null) : QueryEvent;

/// <summary>
/// Goal 预算警告 — 总尝试轮次、Token 或墙钟消耗跨越阈值时发布；
/// 与全模式通用的 <see cref="BudgetWarningEvent"/> 区分：本事件携带 Goal 专属的尝试轮次与耗时。
/// </summary>
public sealed record GoalBudgetWarningEvent(
    OneCode.Core.Goals.GoalBudgetWarningLevel Level,
    int TotalAttempts,
    long TotalTokens,
    TimeSpan? Elapsed) : QueryEvent;

/// <summary>文件修改事件 — 携带增量 Diff；AgentName 标识多代理工作流中的执行者。</summary>
public sealed record FileChangeEvent(
    string FileName,
    IReadOnlyList<string> AddedLines,
    IReadOnlyList<string> RemovedLines,
    string? AgentName = null) : QueryEvent;

/// <summary>单个 agent 的消息输出（子代理 DAG / Goal 工作流产生）。</summary>
public sealed record AgentMessageEvent(
    string AgentName,
    string Content) : QueryEvent;

/// <summary>已批准计划的 Build run 开始 — 与 <see cref="WorkflowRunEndedEvent"/> 配对使用。</summary>
public sealed record WorkflowRunStartedEvent(
    string PlanId,
    int Revision,
    string ContentHash) : QueryEvent;

/// <summary>已批准计划的 Build run 结束（成功、失败或异常收尾）。</summary>
public sealed record WorkflowRunEndedEvent(string PlanId) : QueryEvent;

/// <summary>工作目录切换通知 — Goal worktree 切换/还原后触发。</summary>
public sealed record WorkingDirectoryChangedEvent(string WorkingDirectory) : QueryEvent;
