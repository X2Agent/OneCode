using OneCode.Core.Agent;
using OneCode.Core.Coordinator;
using OneCode.Core.Domain;
using OneCode.Core.Goals;
using OneCode.Core.Hooks;
using OneCode.Core.Permissions;
using OneCode.Core.Tools;
using OneCode.Core.Tokens;
using Microsoft.Agents.AI;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// PIPE-1: 跨角色共享的安全上下文。
/// 所有 profile 的 RulesBySource/AdditionalWorkingDirectories
/// 必须从同一 PipelineSecurityContext 获取，确保 Worker 与 Main 安全字段等价。
/// </summary>
public sealed record PipelineSecurityContext(
    string WorkingDirectory,
    PermissionMode PermissionMode,
    IReadOnlyDictionary<string, PermissionRuleGroup>? RulesBySource,
    IReadOnlyDictionary<string, AdditionalWorkingDirectory>? AdditionalWorkingDirectories,
    IHookExecutionService? Hook,
    IVerificationProvider? VerificationProvider,
    bool EnableVerification,
    Action<OrchestrationEvent>? OrchestrationEventSink,
    Action<FileChange>? FileChangeCallback,
    string? ModelId,
    string? ProviderId,
    EditTransaction? EditTransaction = null,
    IPermissionChecker? PermissionChecker = null,
    ITokenLedger? TokenLedger = null,
    long? MaxBudgetTokens = null,
    SessionId? ConversationId = null,
    long BudgetBaselineTokens = 0,
    Func<long, long, CancellationToken, Task<ApprovalDecision>>? OnBudgetExceeded = null,
    Action<GoalBudgetWarningLevel, long, long>? OnBudgetWarning = null);

/// <summary>
/// 角色级覆盖 — 仅裁剪工具集与配额，不裁剪安全层。
/// 审批单通道：Ask 一律进入 MAF 审批协议；交互呈现由上层桥承担
/// （Main 流式审批拆分），中间件与管道不持有 broker。
/// </summary>
public sealed record PipelineRoleOverrides(
    int MaxToolCalls,
    string ToolLimitMessage,
    Func<string, bool>? IsToolAllowed = null,
    bool EnableToolApproval = true,
    IEnumerable<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>>? AutoApprovalRules = null,
    // 轮数上限（模型往返次数）；null = 不设轮数上限（迭代上限退回 MaxToolCalls）。
    int? MaxTurns = null);

/// <summary>
/// PIPE-1: 统一的 AgentPipelineOptions 工厂。
/// 消除 Main/Worker 管道能力不对称 — 安全字段在所有 profile 上一致传递，
/// 中间件裁剪与工具集按 <see cref="PipelineProfile"/> 控制。
/// </summary>
public static class AgentPipelineOptionsFactory
{
    /// <summary>
    /// 创建 AgentPipelineOptions。安全字段从 ctx 获取（所有 profile 等价），
    /// 中间件裁剪从 profile 推导，工具集与配额从 overrides 获取。
    /// </summary>
    public static AgentPipelineOptions Create(
        PipelineProfile profile,
        PipelineSecurityContext ctx,
        PipelineRoleOverrides overrides)
    {
        var behavior = PipelineProfileBehavior.For(profile);

        var autoApprovalRules = overrides.AutoApprovalRules
            ?? AutoApprovalRulesFactory.Create(
                ctx.PermissionMode,
                ctx.WorkingDirectory,
                ctx.RulesBySource,
                ctx.AdditionalWorkingDirectories,
                ctx.PermissionChecker);

        var enableVerification = ctx.EnableVerification;
        if (ctx.VerificationProvider is not null)
        {
            var permissionProfile = PermissionProfiles.GetProfile(ctx.PermissionMode);
            enableVerification = permissionProfile.EnableVerification;
        }

        if (!behavior.EnableEditPreChecks)
            enableVerification = false;

        var isToolAllowed = overrides.IsToolAllowed;
        if (isToolAllowed is null && behavior.ReadOnlyToolWhitelist is { Count: > 0 } whitelist)
        {
            var allowed = new HashSet<string>(whitelist, StringComparer.OrdinalIgnoreCase);
            isToolAllowed = toolName => allowed.Contains(toolName);
        }

        var enableToolApproval = overrides.EnableToolApproval && behavior.EnableToolApproval;

        return new AgentPipelineOptions
        {
            WorkingDirectory = ctx.WorkingDirectory,
            EditTransaction = ctx.EditTransaction,
            FileChangeCallback = ctx.FileChangeCallback,
            PermissionChecker = ctx.PermissionChecker,
            PermissionMode = ctx.PermissionMode,
            RulesBySource = ctx.RulesBySource,
            AdditionalWorkingDirectories = ctx.AdditionalWorkingDirectories,
            HookExecutionService = ctx.Hook,

            EnableStateMachine = behavior.EnableStateMachine,
            EnableEditPreChecks = behavior.EnableEditPreChecks,
            EnableToolApproval = enableToolApproval,
            EnableFileMemory = behavior.Has(AgentCapability.FileMemory),
            EnableTodo = behavior.Has(AgentCapability.Todo),

            VerificationProvider = ctx.VerificationProvider,
            EnableVerification = enableVerification,

            AutoApprovalRules = autoApprovalRules,

            MaxToolCalls = overrides.MaxToolCalls,
            MaxTurns = overrides.MaxTurns,
            ToolLimitMessage = overrides.ToolLimitMessage,
            IsToolAllowed = isToolAllowed,

            OrchestrationEventSink = ctx.OrchestrationEventSink,
            ModelId = ctx.ModelId,
            ProviderId = ctx.ProviderId,
            TokenLedger = ctx.TokenLedger,
            ConversationId = ctx.ConversationId,
            MaxBudgetTokens = ctx.MaxBudgetTokens,
            BudgetBaselineTokens = ctx.BudgetBaselineTokens,
            OnBudgetExceeded = ctx.OnBudgetExceeded,
            OnBudgetWarning = ctx.OnBudgetWarning,
        };
    }
}

