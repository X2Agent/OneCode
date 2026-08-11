namespace OneCode.App.Tui;

/// <summary>
/// Base type for all TUI-layer events consumed by OneCodeToplevel.
///
/// QueryEvent → TuiEvent 映射表
///
/// | QueryEvent                | TuiEvent                   | 说明 |
/// |---------------------------|----------------------------|------|
/// | TextDeltaEvent            | TuiTextDelta               | 流式文本增量 |
/// | ThinkingDeltaEvent        | TuiThinkingDelta           | thinking 增量（可见时透传） |
/// | ToolStartEvent            | TuiToolStart               | 工具开始（名称+ID） |
/// | ToolDoneEvent             | TuiToolDone                | 工具完成（结果/错误） |
/// | PermissionCheckEvent      | TuiPermissionCheck         | 权限请求（审批/拒绝） |
/// | ToolPoolReadyEvent        | TuiToolPoolReady           | 工具池初始化完成 |
/// | TurnStartedEvent          | TuiTurnStarted             | 轮次开始 |
/// | TurnCompletedEvent        | TuiTurnCompleted           | 轮次结束（含工具调用标记） |
/// | DoneEvent                 | TuiDone                    | 查询完成（含token统计和终止原因） |
///
/// 映射规则：
/// - 每个 QueryEvent 必须有唯一 TuiEvent 映射
/// - 缺失映射的 QueryEvent 显式标记为待补充，不能静默丢弃
/// - TuiError 是 TUI 内部错误，不对应任何 QueryEvent
/// </summary>
public abstract record TuiEvent;

/// <summary>流式文本增量 — 来自 API 的 text_delta 事件。</summary>
public sealed record TuiTextDelta(string Text) : TuiEvent;

/// <summary>Thinking 增量 — 来自 API 的 thinking_delta。仅在 thinking 可见时透传。</summary>
public sealed record TuiThinkingDelta(string Text) : TuiEvent;

/// <summary>
/// 工具调用开始 — 含工具名称、唯一 ID 和操作目标。
/// ToolInput 携带工具参数摘要（如文件路径、命令），start 阶段即可展示操作目标。
/// </summary>
public sealed record TuiToolStart(string ToolId, string Name, string? ToolInput = null) : TuiEvent;

/// <summary>
/// 工具调用完成 — 含结果/错误和原始输入。
/// IsError=true 时 Result 是错误消息；IsError=false 时 Result 是成功输出。
/// ToolId 用于与对应的 <see cref="TuiToolStart"/> 精确匹配，避免
/// ContinueStreaming 清空状态后导致重复行。
/// </summary>
public sealed record TuiToolDone(string Name, bool IsError, string? Result = null, string? ToolInput = null, string ToolId = "") : TuiEvent;

/// <summary>
/// 权限检查 — 当 QueryEngine 请求用户批准工具执行时触发。
/// TUI 层据此显示权限对话框。
/// </summary>
public sealed record TuiPermissionCheck(string ToolName, bool Allowed, string? DenialReason = null) : TuiEvent;

/// <summary>
/// 工具池就绪 — 所有注册工具初始化完成后触发。
/// TUI 层据此更新可用的工具列表和状态栏提示。
/// </summary>
public sealed record TuiToolPoolReady(int TotalTools, int ReadOnlyTools, int McpTools) : TuiEvent;

/// <summary>
/// 查询完成 — 含 token 统计和终止原因。
/// TerminalReason 表示运行终止的具体原因（正常完成、轮次上限、预算超支、取消、验证失败等）。
/// TransactionRolledBack=true 表示文件变更已回滚（验证失败或取消时）。
/// SessionId: Goal/Team 模式下的 Checkpoint 会话 ID，
/// 用户可凭此 ID 通过 /resume-goal 或 /resume-team 恢复中断的执行。
/// 为 null 表示非 Goal/Team 模式或分解失败未产生会话。
/// </summary>
public sealed record TuiWorkflowRunStarted(
    string PlanId,
    int Revision,
    string ContentHash) : TuiEvent;

public sealed record TuiBuildRunState(
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
    int BlockedTasks = 0) : TuiEvent;

public sealed record TuiBuildDelivery(OneCode.Core.Build.BuildRunResult Result) : TuiEvent;

public sealed record TuiDone(
    int InputTokens,
    int OutputTokens,
    OneCode.Core.Build.BuildTerminalReason TerminalReason = OneCode.Core.Build.BuildTerminalReason.Completed,
    int TurnsCompleted = 0,
    int CacheReadTokens = 0,
    int CacheWriteTokens = 0,
    SessionId? SessionId = null,
    bool TransactionRolledBack = false,
    string? ValidationFailureSummary = null) : TuiEvent;

/// <summary>TUI 内部错误 — 不可恢复的 TUI 级别错误，通常导致界面关闭。</summary>
public sealed record TuiError(string Message) : TuiEvent;

/// <summary>轮次开始 — 每轮 API 查询开始前触发。</summary>
public sealed record TuiTurnStarted(int TurnNumber) : TuiEvent;

/// <summary>轮次结束 — 含是否执行了工具调用的标记。</summary>
public sealed record TuiTurnCompleted(int TurnNumber, bool HadToolCalls) : TuiEvent;

/// <summary>TEAM 模式下 agent 之间的协调消息 (design-spec §4.3)。</summary>
public sealed record TuiAgentCoordination(string FromName, string? FromColor, string ToName, string? ToColor, string? Content) : TuiEvent;

/// <summary>TEAM 模式下单个 agent 的消息输出。</summary>
public sealed record TuiAgentMessage(string AgentName, string? AgentColor, string Content) : TuiEvent;

/// <summary>TEAM 模式下任务分解进度更新。</summary>
public sealed record TuiTeamProgress(
    string Header,
    IReadOnlyList<(string Label, string Detail, string Status)> Tasks,
    string? Footer = null) : TuiEvent;

/// <summary>Plan/Team/Goal 共用的轻量进度投影；主对话只渲染当前用户可理解阶段。</summary>
public sealed record TuiModeProgress(
    WorkingMode Mode,
    string Message,
    ModeProgressState State = ModeProgressState.Running,
    int? Completed = null,
    int? Total = null) : TuiEvent;

public enum ModeProgressState
{
    Running,
    Waiting,
    Completed,
    Failed,
    Paused,
}

/// <summary>Goal 的结构化执行结果，供验证、恢复和测试读取；主对话不直接渲染其内部明细。</summary>
public sealed record TuiGoalResult(
    int Completed,
    int Failed,
    int Skipped,
    int Total,
    bool Committed,
    IReadOnlyList<string> CompletedGoals,
    IReadOnlyList<string> FailedGoals,
    IReadOnlyList<string> SkippedGoals,
    string ValidationSummary) : TuiEvent;

/// <summary>
/// Team 计划审批通知事件（display-only）。审批决策通过 MAF RequestPort 持久化，
/// 不再通过 TUI-side TaskCompletionSource 桥接。TUI 仅展示审批卡片。
/// </summary>
public sealed record TuiTeamPlanApproval(
    string TeamName,
    string Summary,
    IReadOnlyList<string> Tasks,
    IReadOnlyList<string> RequiredGates) : TuiEvent;

/// <summary>TEAM 模式结构化交付报告。仅在 TeamRun 完成质量门禁和事务决策后发出。</summary>
public sealed record TuiTeamDelivery(OneCode.Core.Coordinator.DeliveryReport Report) : TuiEvent;

/// <summary>
/// 文件修改事件 — EditTransactionMiddleware 检测到 Write/Edit 后发射。
/// 携带增量 Diff（added/removed lines），TUI 通过 RenderDiffBlock 渲染。
/// </summary>
public sealed record TuiFileChange(
    string FileName,
    IReadOnlyList<string> AddedLines,
    IReadOnlyList<string> RemovedLines) : TuiEvent;

/// <summary>
/// 下一步提示建议 — turn 完成后由规则引擎生成，TUI 在输入框显示为占位符。
/// 用户按 Tab 接受建议，直接输入则忽略。
/// </summary>
public sealed record TuiSuggestions(IReadOnlyList<string> Items) : TuiEvent;

/// <summary>
/// 上下文压缩建议 — token 使用率首次跨越 70% 告警阈值时触发。
/// TUI 可据此向用户显示建议提醒，引导用户执行 /compact。
/// </summary>
public sealed record TuiCompactSuggested(string Message) : TuiEvent;

/// <summary>
/// 审批请求事件 — TUI 可消费此事件自主渲染审批组件。
/// 与 <see cref="TuiPermissionCheck"/> 的区别：后者是事后通知（决策已完成），
/// 此事件是事前通知（决策尚未发生），TUI 可通过 <see cref="ResponseSource"/>
/// 回传用户决策，未来可演进为完全事件驱动的审批 UX。
/// </summary>
public sealed record TuiApprovalRequest(
    string RequestId,
    string ToolName,
    string? ToolInput = null) : TuiEvent
{
    /// <summary>用于回传用户决策的 TaskCompletionSource。</summary>
    public TaskCompletionSource<OneCode.Core.Permissions.ApprovalDecision> ResponseSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// LSP 诊断变更通知 — 当 LspDiagnosticRegistry 收到新诊断时触发。
/// TUI 层据此刷新状态栏的 LSP 指示器（服务器数/错误数/警告数）。
/// 此事件不携带数据，TUI 通过 TuiContext.GetLspServerStatus/GetLspDiagnostics 拉取最新状态。
/// </summary>
public sealed record TuiLspDiagnosticsChanged : TuiEvent;

/// <summary>
/// 用户提问请求事件 — AskUserQuestionTool 需要与用户交互时触发。
/// TUI 可通过 <see cref="ResponseSource"/> 回传用户的回答。
/// 支持预定义选项（单选）或自由文本输入。
/// </summary>
public sealed record TuiUserQuestionRequest(
    string Question,
    IReadOnlyList<string>? Options = null) : TuiEvent
{
    /// <summary>用于回传用户回答的 TaskCompletionSource。</summary>
    public TaskCompletionSource<string> ResponseSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// 多问题向导请求事件 — AskUserQuestionTool 需要展示多问题向导时触发。
/// TUI 可通过 <see cref="ResponseSource"/> 回传所有问题的答案。
/// 支持混合单选和填空题，可前后导航。
/// </summary>
public sealed record TuiQuestionWizardRequest(
    string Title,
    IReadOnlyList<OneCode.Core.Tools.WizardQuestion> Questions) : TuiEvent
{
    /// <summary>用于回传向导结果的 TaskCompletionSource。</summary>
    public TaskCompletionSource<OneCode.Core.Tools.WizardResult> ResponseSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
