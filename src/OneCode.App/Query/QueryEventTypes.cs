namespace OneCode.App.Query;

/// <summary>Base type for all query-layer events consumed by ChatService / TUI adapter.</summary>
public abstract record QueryEvent;

/// <summary>流式文本增量 — 来自 API 的 text_delta。</summary>
public sealed record TextDeltaEvent(string Text) : QueryEvent;

/// <summary>Thinking 增量 — 来自 API 的 thinking_delta。</summary>
public sealed record ThinkingDeltaEvent(string Text) : QueryEvent;

/// <summary>模型随最终回答一并生成的下一步建议。</summary>
public sealed record SuggestionsEvent(IReadOnlyList<string> Items) : QueryEvent;

/// <summary>工具调用开始 — 含工具名称、唯一 ID 和操作目标（参数摘要）。</summary>
public sealed record ToolStartEvent(string ToolId, string ToolName, string? ToolInput = null) : QueryEvent;

/// <summary>工具调用完成 — 含结果/错误和原始输入。</summary>
public sealed record ToolDoneEvent(string ToolId, string ToolName, bool IsError, string? Result = null, string? ToolInput = null) : QueryEvent;

/// <summary>权限检查 — 当 ChatService 需要用户批准工具执行时触发。</summary>
public sealed record PermissionCheckEvent(string ToolName, bool Allowed, string? DenialReason = null) : QueryEvent;

/// <summary>工具池就绪 — 所有注册工具初始化完成后触发。</summary>
public sealed record ToolPoolReadyEvent(int TotalTools, int ReadOnlyTools, int McpTools) : QueryEvent;

/// <summary>Persisted BuildRun state projection. DoneEvent remains stream completion only.</summary>
public sealed record BuildRunStateEvent(
    OneCode.Core.Build.BuildRunId RunId,
    OneCode.Core.Build.BuildRunState State,
    long SequenceNumber,
    IReadOnlyList<string> ClarificationQuestions,
    int CompletedTasks = 0,
    int TotalTasks = 0,
    OneCode.Core.Build.BuildTerminalReason? TerminalReason = null,
    string? FailureSummary = null,
    OneCode.Core.Build.BuildScopeSnapshot? Scope = null,
    OneCode.Core.Build.BuildValidationStatus? ValidationStatus = null,
    int ChangedFiles = 0,
    int TurnsCompleted = 0,
    decimal? EstimatedCost = null,
    int ActiveTasks = 0,
    int BlockedTasks = 0) : QueryEvent
{
    public static BuildRunStateEvent From(OneCode.Core.Build.BuildRun run) => new(
        run.Id,
        run.State,
        run.SequenceNumber,
        run.ClarificationQuestions,
        run.Plan?.Tasks.Count(task => task.Status == OneCode.Core.Build.BuildTaskStatus.Completed) ?? 0,
        run.Plan?.Tasks.Count ?? 0,
        run.TerminalReason,
        run.FailureSummary,
        run.Scope,
        run.Validations.LastOrDefault()?.Status,
        run.ChangedFiles.Count,
        run.Metrics.TurnsCompleted,
        run.Metrics.EstimatedCost,
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
    OneCode.Core.Build.BuildTerminalReason TerminalReason,
    SessionId? SessionId = null,
    bool TransactionRolledBack = false,
    string? ValidationFailureSummary = null) : QueryEvent;

public sealed record UsageUpdateEvent(TokenUsage Usage) : QueryEvent;

/// <summary>轮次开始 — 每轮 API 查询开始前触发。</summary>
public sealed record TurnStartedEvent(int TurnNumber) : QueryEvent;

/// <summary>轮次结束 — 含是否执行了工具调用的标记。</summary>
public sealed record TurnCompletedEvent(int TurnNumber, bool HadToolCalls) : QueryEvent;

/// <summary>API/网络错误 — 不中断对话循环，可由 TUI 显示后继续等待用户输入。</summary>
public sealed record ErrorEvent(string Message, bool Recoverable = true) : QueryEvent;

/// <summary>
/// 事件驱动审批：将 MAF ToolApprovalRequestContent 转为流式事件，
/// 由 TUI 自主渲染审批组件。
/// 决策通过 <see cref="ResponseSource"/> 回调返回。
/// </summary>
public sealed record ApprovalRequestEvent(
    string RequestId,
    string ToolName,
    string? ToolInput = null,
    string? Prompt = null) : QueryEvent
{
    /// <summary>用于回传用户决策的 TaskCompletionSource。</summary>
    public TaskCompletionSource<OneCode.Core.Permissions.ApprovalDecision> ResponseSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
