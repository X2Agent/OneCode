using Microsoft.Extensions.AI;
using OneCode.Core.Goals;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Agent;

/// <summary>
/// Options for MainAgentRunner.
/// </summary>
public sealed record MainAgentRunOptions
{
    public string? ModelId { get; init; }
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Shared harness instructions, passed to MAF as <c>HarnessInstructions</c> so the framework
    /// composes them with <see cref="SystemPrompt"/> (harness first, then the agent body).
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="SystemPrompt"/> rather than pre-joined: MAF owns the composition
    /// order, and a pre-joined string would enter the agent body as well, duplicating the fragment.
    /// </remarks>
    public string? HarnessInstructions { get; init; }
    public string? UserPrompt { get; init; }

    /// <summary>
    /// Pre-built user message (may contain multimodal content like images).
    /// When set, takes priority over <see cref="UserPrompt"/> in <see cref="MainAgentRunner"/>'s
    /// message assembly.
    /// </summary>
    public ChatMessage? UserMessage { get; init; }

    public IReadOnlyList<ChatMessage>? Messages { get; init; }
    public string? WorkingDirectory { get; init; }
    public int MaxTurns { get; init; } = 100;
    public int? MaxOutputTokens { get; init; }
    public bool EnableThinking { get; init; }
    public int ThinkingBudgetTokens { get; init; }
    public string? ThinkingEffort { get; init; }
    public IList<AITool>? Tools { get; init; }

    /// <summary>Immutable upper bound for tool visibility and child-agent inheritance.</summary>
    public ToolCapabilitySet? ToolCapabilities { get; init; }

    /// <summary>Maximum token budget (input + output; null = unlimited).</summary>
    public long? MaxBudgetTokens { get; init; }

    /// <summary>
    /// 当前 UI 工作模式（Build/Plan/Goal）。
    /// 用于 AgentModeProvider 注入模式特定指令到 LLM。
    /// 默认 Build；调用方应根据 modeController.Mode 传入。
    /// </summary>
    public WorkingMode WorkingMode { get; init; } = WorkingMode.Build;

    /// <summary>
    /// 共享编辑事务（可选）。GOAL 模式下由 Goal 工作流 Runtime 按子目标创建并传入。
    /// </summary>
    public EditTransaction? SharedTransaction { get; init; }

    /// <summary>
    /// When true, a successfully validated owned transaction is returned to the caller
    /// without committing. Build mode uses this to persist its final commit decision
    /// before calling <see cref="EditTransaction.Commit"/>.
    /// </summary>
    public bool DeferTransactionCommit { get; init; }

    /// <summary>
    /// Durable lifecycle hook invoked immediately before final validation starts.
    /// Build mode uses it to persist the Verifying state before validation work runs.
    /// </summary>
    public Func<CancellationToken, Task>? BeforeFinalValidation { get; init; }

    /// <summary>
    /// 文件变更回调（可选）。每次 Write/Edit 后通过此回调发射 TuiFileChange 事件，
    /// 供 TUI 层实时渲染 Diff 块。默认 null 时不发射事件（用于测试/非交互场景）。
    /// </summary>
    public Action<FileChange>? FileChangeCallback { get; init; }
    /// <summary>User-configured permission rules keyed by source name.</summary>
    public IReadOnlyDictionary<string, PermissionRuleGroup>? PermissionRules { get; init; }

    /// <summary>Additional working directories for path validation.</summary>
    public IReadOnlyDictionary<string, AdditionalWorkingDirectory>? AdditionalWorkingDirectories { get; init; }

    /// <summary>
    /// 编排事件回调。当设置时，Pipeline 中间件会发射 OrchestrationEvent.ToolStart/ToolDone
    /// 事件到此 sink，供 GOAL 等编排模式实时推送工具调用进度到 TUI。
    /// 为 null 时不发射事件（Build/Plan 模式默认不设置）。
    /// </summary>
    public Action<OneCode.Core.Coordinator.OrchestrationEvent>? OrchestrationEventSink { get; init; }

    /// <summary>Stable conversation identity for all runtime state and persistence.</summary>
    public SessionId? ConversationId { get; init; }

    /// <summary>
    /// Approved-plan 执行世代的历史播种边界（受控 Build run 专用）。
    /// null = 全量转录播种（交互路径）；非 null 时历史 provider 保留 Plan 阶段的只读检索结果，
    /// 仅剥离边界之前的 <c>SubmitPlan</c> 工具调用对。
    /// </summary>
    public HistorySeedBoundary? HistorySeedBoundary { get; init; }

    /// <summary>
    /// 预置进会话待办清单的计划步骤（受控 Build run 专用）。清单为空时才写入，续跑保留已有进度；
    /// 步骤进度的唯一事实源是 agent 自己的待办清单。
    /// </summary>
    public IReadOnlyList<OneCode.Core.Build.BuildPlanTask>? SeedTodoTasks { get; init; }

    /// <summary>Stable identity for tool-batch evidence produced by this agent run.</summary>
    public string? AgentRunId { get; init; }

    /// <summary>Approval broker for conditional tools in the current run.</summary>
    public IApprovalBroker? ApprovalBroker { get; init; }

    /// <summary>
    /// Hard allow-list gate evaluated by PermissionAndLimitMiddleware before any permission rule.
    /// Null = no gate (existing behavior). Controlled Build sets it to the approved tool policy
    /// so tools outside the approved policy fail closed before they can reach the approval path.
    /// </summary>
    public Func<string, bool>? IsToolAllowed { get; init; }

    /// <summary>
    /// 为 true 时，MainAgentRunner 不包装交互式 ApprovalBroker（ForQuery），直接走底层权限检查。
    /// 供 Goal 子目标与后台 Cron 等无人值守执行路径使用。
    /// </summary>
    public bool SuppressToolApproval { get; init; }

    /// <summary>
    /// 权限审批挂起回调（可选）。在 TUI 审批请求下发前触发 Notification hook
    /// （matcher=permission_prompt，ToolName=工具名）。由 ApprovalBroker 调用；
    /// hook 结果不影响审批流程（无阻断语义）。
    /// </summary>
    public Func<string, CancellationToken, Task>? OnPermissionPrompt { get; init; }

    /// <summary>
    /// 预算 70%/90% 预警回调（参数：级别、超出基线的已消耗量、预算上限）。MainAgentRunner
    /// 交互路径默认实现为向事件流写 <c>BudgetWarningEvent</c>；无人值守路径不设置。
    /// </summary>
    public Action<GoalBudgetWarningLevel, long, long>? OnBudgetWarning { get; init; }

    /// <summary>
    /// 预算 100% 消耗时的用户询问回调（交互模式）。返回 AllowOnce=追加预算继续 /
    /// AllowAlways=收尾总结 / Deny=直接停止。无人值守路径（SuppressToolApproval）保持
    /// null → BudgetGuard 硬停短路。
    /// </summary>
    public Func<long, long, CancellationToken, Task<ApprovalDecision>>? OnBudgetExceeded { get; init; }

    /// <summary>
    /// 转换为无人值守运行选项（禁用交互式审批弹窗与预算询问挂起），供 Goal 子目标等非交互执行路径复用。
    /// </summary>
    public MainAgentRunOptions AsUnattended()
        => this with
        {
            ApprovalBroker = null,
            SuppressToolApproval = true,
            OnPermissionPrompt = null,
            OnBudgetExceeded = null,
        };
}

/// <summary>
/// Result from MainAgentRunner.
/// </summary>
public sealed record MainAgentRunResult(
    string? Text,
    long TotalInputTokens,
    long TotalOutputTokens,
    int TurnCount,
    bool BudgetExceeded = false,
    string? BudgetExceededReason = null,
    long CacheReadTokens = 0,
    long CacheWriteTokens = 0,
    long ReasoningTokens = 0,
    OneCode.Core.Workflows.RunTerminalReason TerminalReason = OneCode.Core.Workflows.RunTerminalReason.Completed,
    bool TransactionCommitted = false,
    bool TransactionRolledBack = false,
    OneCode.Core.Build.BuildValidationStatus FinalValidationStatus = OneCode.Core.Build.BuildValidationStatus.Skipped,
    IReadOnlyList<string>? ModifiedFiles = null,
    string? ValidationFailureSummary = null,
    IReadOnlyList<CompletedToolBatch>? CompletedToolBatches = null,
    // 工具调用总数（run.json metrics.toolCalls 的数据源；默认 0 保持既有调用方兼容）。
    int ToolCallCount = 0);


