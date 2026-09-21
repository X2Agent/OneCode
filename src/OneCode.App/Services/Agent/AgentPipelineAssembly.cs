using OneCode.Core.Tokens;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Middleware.Contracts;

namespace OneCode.App.Services.Agent;

/// <summary>
/// Unified pipeline security + options assembly for Main and sub-agent paths.
/// Replaces the split between <c>AgentPipelineSecurityBuilder</c> and ad-hoc
/// <see cref="AgentPipelineOptionsFactory"/> call sites.
/// </summary>
public sealed class AgentPipelineAssembly(
    Core.Tools.IWorkingDirectoryAccessor workingDirectoryAccessor,
    IHookExecutionService hookExecutionService,
    Core.Tools.IVerificationProvider verificationProvider,
    IPermissionModeProvider modeProvider,
    IPermissionChecker permissionChecker,
    ITokenLedger tokenLedger)
{
    /// <summary>
    /// Builds <see cref="AgentPipelineOptions"/> for the Main agent (<see cref="PipelineProfile.Full"/>).
    /// </summary>
    public AgentPipelineOptions BuildMainOptions(
        MainAgentRunOptions options,
        EditTransaction transaction,
        string cwd,
        string? modelId,
        string? providerId)
    {
        var securityContext = BuildMainSecurityContext(cwd, options, transaction, modelId, providerId);
        var roleOverrides = BuildMainRoleOverrides(options);
        return AgentPipelineOptionsFactory.Create(PipelineProfile.Full, securityContext, roleOverrides);
    }

    /// <summary>
    /// Builds security context for the Main agent path.
    /// </summary>
    public PipelineSecurityContext BuildMainSecurityContext(
        string cwd,
        MainAgentRunOptions options,
        EditTransaction transaction,
        string? modelId,
        string? providerId)
    {
        return PipelineSecurityContextBuilder.Create(
            workingDirectory: cwd,
            permissionMode: modeProvider.CurrentMode,
            hook: hookExecutionService,
            permissionChecker: permissionChecker,
            tokenLedger: tokenLedger,
            rulesBySource: options.PermissionRules,
            additionalWorkingDirectories: BuildAdditionalWorkingDirectories(),
            verificationProvider: verificationProvider,
            enableVerification: PermissionProfiles.GetProfile(modeProvider.CurrentMode).EnableVerification,
            orchestrationEventSink: options.OrchestrationEventSink,
            fileChangeCallback: options.FileChangeCallback,
            modelId: modelId,
            providerId: providerId,
            behaviorContracts: CreateDefaultBehaviorContracts(cwd),
            editTransaction: transaction,
            conversationId: options.ConversationId,
            maxBudgetTokens: options.MaxBudgetTokens);
    }

    /// <summary>
    /// Builds role-level overrides for the Main agent path.
    /// </summary>
    public PipelineRoleOverrides BuildMainRoleOverrides(MainAgentRunOptions options)
    {
        return new PipelineRoleOverrides(
            MaxToolCalls: options.MaxTurns,
            ToolLimitMessage: $"Maximum tool call limit ({options.MaxTurns}) reached.",
            IsToolAllowed: options.IsToolAllowed,
            // GoalAuto（GOAL 子目标路径）下 broker 为 null，但必须挂 MAF 审批中间件 +
            // 全放行规则（AutoApprovalRulesFactory 对 GoalAuto 自动放行全部工具），
            // 否则 LoopAgent 会因 pending tool approval 无人解析而直接停止。
            // SuppressToolApproval 保留受控 Build（broker=null + 显式禁用）的语义。
            //
            // 该布尔同时门控「工具是否带原生审批标记」：只有真正存在审批通道时才能标记，
            // 否则每个函数调用都会变成无人应答的审批请求。因此 SuppressToolApproval 时
            // 标记必须一并关闭——这与它“不挂交互 broker”的意图一致。
            EnableToolApproval: (options.ApprovalBroker is not null
                || modeProvider.CurrentMode == PermissionMode.GoalAuto)
                && !options.SuppressToolApproval);
    }

    /// <summary>
    /// Creates default behavior contracts (FileEdit) shared by Main and Team paths.
    /// </summary>
    internal static IReadOnlyList<FileEditContract> CreateDefaultBehaviorContracts(string workingDirectory) =>
    [
        new FileEditContract(workingDirectory),
    ];

    private Dictionary<string, AdditionalWorkingDirectory>? BuildAdditionalWorkingDirectories()
    {
        var dirs = workingDirectoryAccessor.AdditionalDirectories;
        if (dirs is null || dirs.Count == 0)
            return null;

        var dict = new Dictionary<string, AdditionalWorkingDirectory>(StringComparer.Ordinal);
        for (var i = 0; i < dirs.Count; i++)
        {
            dict[$"dir-{i}"] = new AdditionalWorkingDirectory(dirs[i], WorkingDirectorySource.Config);
        }

        return dict;
    }
}
