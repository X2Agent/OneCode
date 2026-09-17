using OneCode.Core.Agent;
using OneCode.Core.Config;
using OneCode.Core.Coordinator;
using OneCode.Infrastructure.Agent;
using OneCode.Core.Tokens;

namespace OneCode.App.Services.Agent;

/// <summary>
/// Request to assemble a sub-agent pipeline (Forked / Team member).
/// </summary>
public sealed record SubAgentPipelineRequest
{
    public required PipelineProfile Profile { get; init; }
    public required string WorkingDirectory { get; init; }
    public EditTransaction? EditTransaction { get; init; }
    public required int MaxToolCalls { get; init; }
    public string? ModelId { get; init; }
    public string? ProviderId { get; init; }
    public SessionId? ConversationId { get; init; }
    public IReadOnlyList<string>? AllowedTools { get; init; }
    public Action<OrchestrationEvent>? OrchestrationEventSink { get; init; }
    public Action<FileChange>? FileChangeCallback { get; init; }
    public IApprovalBroker? ApprovalBroker { get; init; }
    public string? TeamMemberId { get; init; }
}

/// <summary>
/// Unified sub-agent pipeline factory — single assembly path for Forked and Team agents.
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
    private long? MaxBudgetTokens => configManager.Current.Effective.MaxBudgetTokens;

    /// <summary>
    /// Builds <see cref="AgentPipelineOptions"/> for a sub-agent profile.
    /// </summary>
    public AgentPipelineOptions BuildOptions(SubAgentPipelineRequest request)
    {
        var securityContext = BuildSecurityContext(request);
        var roleOverrides = BuildRoleOverrides(request);
        return AgentPipelineOptionsFactory.Create(request.Profile, securityContext, roleOverrides);
    }

    private PipelineSecurityContext BuildSecurityContext(SubAgentPipelineRequest request)
    {
        var permCtx = appStateAccessor.Current?.ToolPermissionContext;
        var cwd = request.WorkingDirectory;

        // One capability lookup drives every profile-dependent branch below. Previously this method
        // switched on request.Profile four separate times, so the profile's policy had to be kept
        // consistent by hand across all four.
        var behavior = PipelineProfileBehavior.For(request.Profile);

        var permissionMode = request.Profile == PipelineProfile.TeamMember
            ? PermissionMode.Team
            : modeProvider.CurrentMode;

        // Capability decides whether verification applies at all; the permission profile then decides
        // whether it is active. Both gates apply (permission profile alone would enable it for read-only
        // profiles, capability alone would bypass the user's permission mode).
        var useVerification = behavior.Has(AgentCapability.Verification);
        var enableVerification = useVerification
            && PermissionProfiles.GetProfile(permissionMode).EnableVerification;

        var verificationProviderForProfile = useVerification ? verificationProvider : null;
        var behaviorContracts = behavior.Has(AgentCapability.BehaviorContracts)
            ? AgentPipelineAssembly.CreateDefaultBehaviorContracts(cwd)
            : null;

        return PipelineSecurityContextBuilder.Create(
            workingDirectory: cwd,
            permissionMode: permissionMode,
            hook: hookExecutionService,
            permissionChecker: permissionChecker,
            tokenLedger: tokenLedger,
            rulesBySource: permCtx?.RulesBySource,
            additionalWorkingDirectories: permCtx?.AdditionalWorkingDirectories,
            sessionAllowlist: permCtx?.SessionAllowlist,
            verificationProvider: verificationProviderForProfile,
            enableVerification: enableVerification,
            orchestrationEventSink: request.OrchestrationEventSink,
            fileChangeCallback: request.FileChangeCallback,
            modelId: request.ModelId,
            providerId: request.ProviderId,
            behaviorContracts: behaviorContracts,
            editTransaction: request.EditTransaction,
            conversationId: request.ConversationId,
            maxBudgetTokens: MaxBudgetTokens);
    }

    private static PipelineRoleOverrides BuildRoleOverrides(SubAgentPipelineRequest request)
    {
        var profile = request.Profile;
        var memberLabel = request.TeamMemberId ?? "sub-agent";

        Func<string, bool>? isToolAllowed = null;
        if (request.AllowedTools is { Count: > 0 } allowed)
        {
            var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
            isToolAllowed = toolName => allowedSet.Contains(toolName);
        }

        return profile switch
        {
            PipelineProfile.TeamMember => new PipelineRoleOverrides(
                MaxToolCalls: request.MaxToolCalls,
                ToolLimitMessage: $"Team member '{memberLabel}' tool call limit ({request.MaxToolCalls}) reached.",
                IsToolAllowed: isToolAllowed,
                EnableToolApproval: false,
                ApprovalBroker: request.ApprovalBroker),
            PipelineProfile.Explore or PipelineProfile.Plan => new PipelineRoleOverrides(
                MaxToolCalls: request.MaxToolCalls,
                ToolLimitMessage: $"Sub-agent tool call limit ({request.MaxToolCalls}) reached.",
                IsToolAllowed: isToolAllowed),
            _ => new PipelineRoleOverrides(
                MaxToolCalls: request.MaxToolCalls,
                ToolLimitMessage: $"Sub-agent tool call limit ({request.MaxToolCalls}) reached.",
                IsToolAllowed: isToolAllowed),
        };
    }
}
