using OneCode.Core.Coordinator;
using OneCode.Infrastructure.Agent;
using OneCode.Core.Cost;
using OneCode.Infrastructure.Config;

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
    ICostTracker costTracker,
    IConfigManager configManager)
{
    private decimal? MaxBudgetUsd => (decimal?)configManager.Current.Effective.MaxBudgetUsd;

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

        var permissionMode = request.Profile switch
        {
            PipelineProfile.TeamMember => PermissionMode.Team,
            _ => modeProvider.CurrentMode,
        };

        var enableVerification = request.Profile switch
        {
            PipelineProfile.TeamMember or PipelineProfile.Explore or PipelineProfile.Plan => false,
            _ => PermissionProfiles.GetProfile(permissionMode).EnableVerification,
        };

        var verificationProviderForProfile = request.Profile switch
        {
            PipelineProfile.TeamMember or PipelineProfile.Explore or PipelineProfile.Plan => null,
            _ => verificationProvider,
        };

        var behaviorContracts = request.Profile switch
        {
            PipelineProfile.Explore or PipelineProfile.Plan => null,
            _ => AgentPipelineAssembly.CreateDefaultBehaviorContracts(cwd),
        };

        return PipelineSecurityContextBuilder.Create(
            workingDirectory: cwd,
            permissionMode: permissionMode,
            hook: hookExecutionService,
            permissionChecker: permissionChecker,
            costTracker: costTracker,
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
            maxBudgetUsd: MaxBudgetUsd);
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
