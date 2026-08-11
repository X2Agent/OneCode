using Microsoft.Extensions.AI;
using OneCode.App.Tui;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Agent;

/// <summary>
/// <see cref="MainAgentRunner"/> 的运行选项与结果契约。
/// 抽取自 MainAgentRunner.cs 以满足单文件行数规范。
/// </summary>

/// <summary>
/// Options for MainAgentRunner.
/// </summary>
public sealed record MainAgentRunOptions
{
    public string? ModelId { get; init; }
    public string? SystemPrompt { get; init; }
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

    /// <summary>Maximum cost budget in USD (null = unlimited).</summary>
    public decimal? MaxBudgetUsd { get; init; }

    /// <summary>
    /// 当前 UI 工作模式（Build/Plan/Team/Goal）。
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

    // Permission context
    // These fields populate ToolPermissionContext so that PermissionChecker
    // strategies can evaluate rules and validate paths. When null, defaults
    // (empty collections) are used, preserving existing behavior.

    /// <summary>User-configured permission rules keyed by source name.</summary>
    public IReadOnlyDictionary<string, PermissionRuleGroup>? PermissionRules { get; init; }

    /// <summary>Additional working directories for path validation.</summary>
    public IReadOnlyDictionary<string, AdditionalWorkingDirectory>? AdditionalWorkingDirectories { get; init; }

    /// <summary>Session-level allowlist (e.g., from "Always Allow" user choice).</summary>
    public HashSet<string>? SessionAllowlist { get; init; }


    /// <summary>
    /// 编排事件回调。当设置时，Pipeline 中间件会发射 OrchestrationEvent.ToolStart/ToolDone
    /// 事件到此 sink，供 GOAL/Team 等编排模式实时推送工具调用进度到 TUI。
    /// 为 null 时不发射事件（Build/Plan 模式默认不设置）。
    /// </summary>
    public Action<OneCode.Core.Coordinator.OrchestrationEvent>? OrchestrationEventSink { get; init; }

    /// <summary>Stable conversation identity for all runtime state and persistence.</summary>
    public SessionId? ConversationId { get; init; }

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
    /// When true, MainAgentRunner must not wrap an interactive ApprovalBroker (ForQuery). Used by
    /// controlled Build: tool permissions were confirmed up-front at the plan gate, so attempt
    /// execution must never suspend on an in-process TaskCompletionSource approval dialog.
    /// </summary>
    public bool SuppressToolApproval { get; init; }
}

/// <summary>
/// Result from MainAgentRunner.
/// </summary>
/// <param name="CacheReadTokens">缓存读 token（命中部分）。从 UsageDetails.CachedInputTokenCount 提取。</param>
/// <param name="CacheWriteTokens">缓存写 token（Anthropic 创生）。从 AdditionalCounts["cache_creation_input_tokens"] 提取。</param>
/// <param name="ReasoningTokens">推理 token（思考）。从 UsageDetails.ReasoningTokenCount 提取。</param>
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
    OneCode.Core.Build.BuildTerminalReason TerminalReason = OneCode.Core.Build.BuildTerminalReason.Completed,
    bool TransactionCommitted = false,
    bool TransactionRolledBack = false,
    OneCode.Core.Build.BuildValidationStatus FinalValidationStatus = OneCode.Core.Build.BuildValidationStatus.Skipped,
    IReadOnlyList<string>? ModifiedFiles = null,
    string? ValidationFailureSummary = null,
    IReadOnlyList<CompletedToolBatch>? CompletedToolBatches = null);


