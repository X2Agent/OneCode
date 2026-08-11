using OneCode.Core.Coordinator;
using OneCode.Core.Domain;
using OneCode.Core.Hooks;
using OneCode.Core.Permissions;
using OneCode.Core.Tools;
using OneCode.Core.Cost;
using OneCode.Infrastructure.Middleware;
using OneCode.Infrastructure.Middleware.Contracts;
using Microsoft.Agents.AI;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// PIPE-1: 跨角色共享的安全上下文。
/// 所有 profile 的 SafetyInvariants/BehaviorContracts/RulesBySource/AdditionalWorkingDirectories/SessionAllowlist
/// 必须从同一 PipelineSecurityContext 获取，确保 Worker/Team 与 Main 安全字段等价。
/// </summary>
public sealed record PipelineSecurityContext(
    string WorkingDirectory,
    PermissionMode PermissionMode,
    IReadOnlyDictionary<string, PermissionRuleGroup>? RulesBySource,
    IReadOnlyDictionary<string, AdditionalWorkingDirectory>? AdditionalWorkingDirectories,
    HashSet<string>? SessionAllowlist,
    IHookExecutionService? Hook,
    IVerificationProvider? VerificationProvider,
    bool EnableVerification,
    Action<OrchestrationEvent>? OrchestrationEventSink,
    Action<FileChange>? FileChangeCallback,
    string? ModelId,
    string? ProviderId,
    IReadOnlyList<ISafetyInvariant>? SafetyInvariants = null,
    IReadOnlyList<FileEditContract>? BehaviorContracts = null,
    EditTransaction? EditTransaction = null,
    IPermissionChecker? PermissionChecker = null,
    ICostTracker? CostTracker = null,
    decimal? MaxBudgetUsd = null,
    SessionId? ConversationId = null);

/// <summary>
/// 角色级覆盖 — 仅裁剪工具集与配额，不裁剪安全层。
/// ApprovalHandler 用于 Team 路径的 inline 审批（MAF workflow manager 无法处理 ToolApprovalRequestContent）。
/// </summary>
public sealed record PipelineRoleOverrides(
    int MaxToolCalls,
    string ToolLimitMessage,
    Func<string, bool>? IsToolAllowed = null,
    bool EnableToolApproval = true,
    IEnumerable<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>>? AutoApprovalRules = null,
    VerificationOptions? VerificationOptions = null,
    Func<string, JsonElement, CancellationToken, Task<bool>>? ApprovalHandler = null,
    IApprovalBroker? ApprovalBroker = null);

/// <summary>
/// PIPE-1: 统一的 AgentPipelineOptions 工厂。
/// 消除 Main/Worker/Team 三路管道能力不对称 — 安全字段在所有 profile 上一致传递，
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
            ?? AutoApprovalRulesFactory.Create(ctx.PermissionMode);

        var enableVerification = ctx.EnableVerification;
        if (ctx.VerificationProvider is not null)
        {
            var permissionProfile = PermissionProfiles.GetProfile(ctx.PermissionMode);
            enableVerification = permissionProfile.EnableVerification;
        }

        if (!behavior.EnableBehaviorContracts)
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
            SessionAllowlist = ctx.SessionAllowlist,
            HookExecutionService = ctx.Hook,
            BehaviorContracts = behavior.EnableBehaviorContracts
                ? ctx.BehaviorContracts ?? [new FileEditContract(ctx.WorkingDirectory)]
                : null,
            SafetyInvariants = ctx.SafetyInvariants,

            EnableStateMachine = behavior.EnableStateMachine,
            EnableTaskRecovery = behavior.EnableTaskRecovery,
            EnableBehaviorContracts = behavior.EnableBehaviorContracts,
            EnableToolApproval = enableToolApproval,

            VerificationProvider = ctx.VerificationProvider,
            EnableVerification = enableVerification,
            VerificationOptions = overrides.VerificationOptions,

            AutoApprovalRules = autoApprovalRules,
            ApprovalBroker = overrides.ApprovalBroker,
            ApprovalHandler = overrides.ApprovalHandler,

            MaxToolCalls = overrides.MaxToolCalls,
            ToolLimitMessage = overrides.ToolLimitMessage,
            IsToolAllowed = isToolAllowed,

            OrchestrationEventSink = ctx.OrchestrationEventSink,
            ModelId = ctx.ModelId,
            ProviderId = ctx.ProviderId,
            CostTracker = ctx.CostTracker,
            ConversationId = ctx.ConversationId,
            MaxBudgetUsd = ctx.MaxBudgetUsd,
        };
    }

    /// <summary>
    /// 为 Worker 角色创建标准 pipeline options（便捷重载）。
    /// </summary>
    public static AgentPipelineOptions CreateForWorker(
        PipelineSecurityContext ctx,
        int maxToolCalls,
        Func<string, bool>? isToolAllowed = null,
        string? modelId = null,
        string? providerId = null,
        PipelineProfile profile = PipelineProfile.Worker)
    {
        var fullCtx = ctx with
        {
            ModelId = modelId ?? ctx.ModelId,
            ProviderId = providerId ?? ctx.ProviderId,
        };

        return Create(profile, fullCtx, new PipelineRoleOverrides(
            MaxToolCalls: maxToolCalls,
            ToolLimitMessage: $"Sub-agent tool call limit ({maxToolCalls}) reached.",
            IsToolAllowed: isToolAllowed));
    }
}
