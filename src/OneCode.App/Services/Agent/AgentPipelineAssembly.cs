using Microsoft.Agents.AI;
using OneCode.Core.Cost;
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
    ICostTracker costTracker)
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
            costTracker: costTracker,
            rulesBySource: options.PermissionRules,
            additionalWorkingDirectories: BuildAdditionalWorkingDirectories(),
            sessionAllowlist: options.SessionAllowlist,
            verificationProvider: verificationProvider,
            enableVerification: PermissionProfiles.GetProfile(modeProvider.CurrentMode).EnableVerification,
            orchestrationEventSink: options.OrchestrationEventSink,
            fileChangeCallback: options.FileChangeCallback,
            modelId: modelId,
            providerId: providerId,
            behaviorContracts: CreateDefaultBehaviorContracts(cwd),
            editTransaction: transaction,
            conversationId: options.ConversationId,
            maxBudgetUsd: options.MaxBudgetUsd);
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
            AutoApprovalRules: CreateAutoApprovalRules(),
            EnableToolApproval: options.ApprovalBroker is not null,
            ApprovalBroker: options.ApprovalBroker);
    }

    /// <summary>
    /// Creates default behavior contracts (FileEdit) shared by Main and Team paths.
    /// </summary>
    internal static IReadOnlyList<FileEditContract> CreateDefaultBehaviorContracts(string workingDirectory) =>
    [
        new FileEditContract(workingDirectory),
    ];

    /// <summary>
    /// Mode-aware auto-approval rules for MAF ToolApprovalAgent.
    /// Prepends MAF <see cref="AgentSkillsProvider"/> read-only tool rules.
    /// </summary>
    internal List<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>> CreateAutoApprovalRules()
    {
        var rules = AutoApprovalRulesFactory.Create(modeProvider.CurrentMode);
        rules.Insert(0, AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule);
        return rules;
    }

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
