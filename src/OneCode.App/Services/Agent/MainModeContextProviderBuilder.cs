using Microsoft.Agents.AI;
using OneCode.App.Services.BuildMode;
using OneCode.App.Services.GoalMode;
using OneCode.App.Services.PlanMode;
using OneCode.App.Tui;
using OneCode.Core.Prompt;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Agent;

/// <summary>Builds mode-specific context providers for the main agent path.</summary>
public sealed class MainModeContextProviderBuilder(
    SharedContextProviderBuilder sharedBuilder,
    IPlanModeService planModeService,
    IPermissionModeProvider modeProvider,
    IPromptManager promptManager,
    BuildModeAttachmentProvider buildModeAttachmentProvider,
    PlanExecutionContextProvider planExecutionContextProvider,
    GoalContextState goalContextState)
{
    /// <summary>Builds full ContextProviders for the Main agent: shared + mode-specific providers.</summary>
    public async Task<List<AIContextProvider>> BuildForMainAsync(
        AgentContextProviderOptions baseOptions,
        WorkingMode workingMode,
        CancellationToken ct)
    {
        var contextProviders = sharedBuilder.BuildCommon(
            SharedContextProviderBuilder.ApplyProfileDefaults(PipelineProfile.Full, baseOptions));

        contextProviders.Add(new PlanModeAttachmentProvider(planModeService));
        contextProviders.Add(buildModeAttachmentProvider);
        contextProviders.Add(planExecutionContextProvider);
        contextProviders.Add(new GoalContextProvider(modeProvider, goalContextState));
        contextProviders.Add(await CreateAgentModeProviderAsync(workingMode, ct).ConfigureAwait(false));

        return contextProviders;
    }

    internal static string ResolveAgentMode(WorkingMode workingMode, PermissionMode? permissionMode)
    {
        return workingMode switch
        {
            WorkingMode.Plan => "plan",
            WorkingMode.Team => "team",
            WorkingMode.Goal => "goal",
            _ => permissionMode == PermissionMode.Plan ? "plan" : "build",
        };
    }

    private async Task<AgentModeProvider> CreateAgentModeProviderAsync(
        WorkingMode workingMode, CancellationToken ct)
    {
        var defaultMode = ResolveAgentMode(workingMode, modeProvider.CurrentMode);

        var promptName = defaultMode switch
        {
            "build" => "system/build",
            "plan" => "system/plan",
            "team" => "system/team",
            "goal" => "system/goal",
            _ => null,
        };

        var modeInstructions = promptName is null
            ? $"[CURRENT MODE: {defaultMode.ToUpperInvariant()}]\nExecute the user's request using all available tools."
            : await promptManager.GetPromptAsync(promptName, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Mode prompt '{promptName}' not found in any PromptManager store.");

        return new AgentModeProvider(new AgentModeProviderOptions
        {
            DefaultMode = defaultMode,
            Modes =
            [
                new AgentModeProviderOptions.AgentMode("build", "Execute tasks directly with full access to all tools."),
                new AgentModeProviderOptions.AgentMode("plan", "Plan first, then execute only after user approval. Only read-only tools are permitted."),
                new AgentModeProviderOptions.AgentMode("team", "Multi-agent team coordination. Report back to the orchestrator."),
                new AgentModeProviderOptions.AgentMode("goal", "Executing a specific sub-goal. Focus on meeting the success criteria."),
            ],
            Instructions = modeInstructions,
        });
    }
}
