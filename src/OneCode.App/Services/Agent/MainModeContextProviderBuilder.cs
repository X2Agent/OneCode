using Microsoft.Agents.AI;
using OneCode.App.Services.BuildMode;
using OneCode.App.Services.Context;
using OneCode.App.Services.GoalMode;
using OneCode.App.Services.PlanMode;

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
        var contextProviders = sharedBuilder.BuildCommon(PipelineProfile.Full, baseOptions);

        // Always inject the current mode system prompt (replaces MAF AgentModeProvider tools).
        contextProviders.Add(await CreateModeInstructionProviderAsync(workingMode, ct).ConfigureAwait(false));

        // W1: register only providers for the active WorkingMode (no full hang + internal no-op).
        switch (workingMode)
        {
            case WorkingMode.Plan:
                contextProviders.Add(new PlanModeAttachmentProvider(planModeService));
                // Approved-plan execution context (also used when leaving Plan into AcceptEdits).
                contextProviders.Add(planExecutionContextProvider);
                break;

            case WorkingMode.Goal:
                contextProviders.Add(new GoalContextProvider(modeProvider, goalContextState));
                break;

            case WorkingMode.Team:
                // Team orchestration context comes from Team path; Main Team mode needs instructions only.
                break;

            case WorkingMode.Build:
            default:
                contextProviders.Add(buildModeAttachmentProvider);
                // PlanExecution gates on AcceptEdits when an approved plan is running under Build.
                contextProviders.Add(planExecutionContextProvider);
                break;
        }

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

    /// <summary>
    /// 构建模式指令注入 Provider——替代原 MAF <see cref="AgentModeProvider"/>。
    /// 只保留指令注入职能（system/{mode} 提示词），不暴露 mode_get/mode_set 工具、
    /// 不维护 MAF session state mode（OneCode 模式由宿主驱动，LLM 驱动切换会双轨不一致）。
    /// </summary>
    private async Task<ModeInstructionProvider> CreateModeInstructionProviderAsync(
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

        return new ModeInstructionProvider(modeInstructions);
    }
}
