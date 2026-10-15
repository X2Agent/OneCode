using OneCode.Core.Agent;
using OneCode.Core.Config;
using OneCode.Core.Coordinator;
using OneCode.Infrastructure.Agent;
using OneCode.Core.Tokens;

namespace OneCode.App.Services.Agent;

/// <summary>
/// Request to assemble a sub-agent pipeline (forked sub-agent).
/// </summary>
public sealed record SubAgentPipelineRequest
{
    public required PipelineProfile Profile { get; init; }
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// 父级（发起 fork 的主代理）工作模式。BUILD 下预算按 fork 启动时点对 ledger 的快照计量
    /// （镜像主路径 AgentPipelineAssembly 的 run 级基线规则）；其余模式维持会话级累计（基线 0）。
    /// </summary>
    public WorkingMode? ParentWorkingMode { get; init; }

    public EditTransaction? EditTransaction { get; init; }
    public required int MaxToolCalls { get; init; }
    /// <summary>轮数上限（模型往返次数）；null = 不设轮数上限（迭代上限退回 MaxToolCalls）。</summary>
    public int? MaxTurns { get; init; }
    public string? ModelId { get; init; }
    public string? ProviderId { get; init; }
    public SessionId? ConversationId { get; init; }
    public IReadOnlyList<string>? AllowedTools { get; init; }
    public Action<OrchestrationEvent>? OrchestrationEventSink { get; init; }
    public Action<FileChange>? FileChangeCallback { get; init; }
}

/// <summary>
/// Unified sub-agent pipeline factory — single assembly path for forked agents.
/// Main agent uses <see cref="AgentPipelineAssembly.BuildMainOptions"/> directly.
/// </summary>
public sealed class SubAgentPipelineFactory(
    IPermissionModeProvider modeProvider,
    IHookExecutionService hookExecutionService,
    IVerificationProvider verificationProvider,
    IPermissionChecker permissionChecker,
    IAppStateAccessor appStateAccessor,
    ITokenLedger tokenLedger,
    IConfigManager configManager)
{
    // 预算上限：0（或负值）= 不设限，归一化为 null（与主路径 ResolveMaxBudgetTokens 同一口径）
    private long? MaxBudgetTokens
    {
        get
        {
            var configured = configManager.Current.Effective.MaxBudgetTokens;
            return configured > 0 ? configured : null;
        }
    }

    /// <summary>
    /// Builds <see cref="AgentPipelineOptions"/> for a sub-agent profile.
    /// </summary>
    public AgentPipelineOptions BuildOptions(SubAgentPipelineRequest request)
    {
        var securityContext = BuildSecurityContext(request);
        var roleOverrides = BuildRoleOverrides(request);
        return AgentPipelineOptionsFactory.Create(request.Profile, securityContext, roleOverrides)
            // 轮次预算收尾提醒对 fork 全路径开启（MaxTurns 缺省时没有可参照的轮数预算，
            // 不开）；主会话管道不经本工厂装配，逐调用计数不会跨用户消息失真。
            with { EnableTurnBudgetNudge = request.MaxTurns is not null };
    }

    private PipelineSecurityContext BuildSecurityContext(SubAgentPipelineRequest request)
    {
        var permCtx = appStateAccessor.Current?.ToolPermissionContext;
        var cwd = request.WorkingDirectory;

        // One capability lookup drives every profile-dependent branch below. Previously this method
        // switched on request.Profile four separate times, so the profile's policy had to be kept
        // consistent by hand across all four.
        var behavior = PipelineProfileBehavior.For(request.Profile);

        var permissionMode = modeProvider.CurrentMode;

        // Capability decides whether verification applies at all; the permission profile then decides
        // whether it is active. Both gates apply (permission profile alone would enable it for read-only
        // profiles, capability alone would bypass the user's permission mode).
        var useVerification = behavior.Has(AgentCapability.Verification);
        var enableVerification = useVerification
            && PermissionProfiles.GetProfile(permissionMode).EnableVerification;

        var verificationProviderForProfile = useVerification ? verificationProvider : null;

        return PipelineSecurityContextBuilder.Create(
            workingDirectory: cwd,
            permissionMode: permissionMode,
            hook: hookExecutionService,
            permissionChecker: permissionChecker,
            tokenLedger: tokenLedger,
            rulesBySource: permCtx?.RulesBySource,
            additionalWorkingDirectories: permCtx?.AdditionalWorkingDirectories,
            verificationProvider: verificationProviderForProfile,
            enableVerification: enableVerification,
            orchestrationEventSink: request.OrchestrationEventSink,
            fileChangeCallback: request.FileChangeCallback,
            modelId: request.ModelId,
            providerId: request.ProviderId,
            editTransaction: request.EditTransaction,
            conversationId: request.ConversationId,
            // BUILD 父级按 run 基线计量（主路径语义），fork 镜像同一规则：以 fork 启动
            // 时点的 ledger 快照为基线，只衡量子代理自身增量——长 Plan 阶段的会话累计
            // 不再把子代理"开跑即触预算硬停"；父级 BudgetGuard 仍以其自身基线约束总额。
            // PLAN/GOAL 维持会话级累计（基线 0）。
            maxBudgetTokens: MaxBudgetTokens,
            budgetBaselineTokens: request.ParentWorkingMode == WorkingMode.Build
                ? tokenLedger.GetTotalTokens()
                : 0);
        // 预算回调有意不传（取舍见 BudgetGuardRunMiddleware 分级策略）：
        // onBudgetExceeded 是交互三选一询问，fork 无人值守必须硬停短路；
        // onBudgetWarning 只驱动主会话预警横幅，fork 的实时进度已由
        // OrchestrationEventSink 上报，父级 BudgetGuard 才是预算的最终裁决方。
    }

    private static PipelineRoleOverrides BuildRoleOverrides(SubAgentPipelineRequest request)
    {
        Func<string, bool>? isToolAllowed = null;
        if (request.AllowedTools is { Count: > 0 } allowed)
        {
            var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
            isToolAllowed = toolName => allowedSet.Contains(toolName);
        }

        // 工具预算触发后的提示必须可指导收尾：只报"达到上限"会让模型继续重试调用，
        // 带上"停止调用并总结"的指令才能换来一次无工具的收尾输出（父代理据此判断 partial）。
        var toolLimitMessage =
            $"Sub-agent tool call budget ({request.MaxToolCalls}) reached. " +
            "Stop calling tools and summarize the work completed so far and what remains.";

        return new PipelineRoleOverrides(
            MaxToolCalls: request.MaxToolCalls,
            ToolLimitMessage: toolLimitMessage,
            IsToolAllowed: isToolAllowed,
            MaxTurns: request.MaxTurns);
    }
}
