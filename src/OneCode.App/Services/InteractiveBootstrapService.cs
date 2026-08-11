using OneCode.App.Tui;

namespace OneCode.App.Services;

/// <summary>
/// Interactive-session bootstrap: trust flow, system prompt, AppState hydration,
/// and slash-command discovery. Extracted from <see cref="InteractiveModeExecutor"/>
/// to keep the orchestrator under the DI constructor-parameter limit.
/// </summary>
public sealed class InteractiveBootstrapService(
    StartupFlowCoordinator startupFlowCoordinator,
    PromptConfigBuilder promptConfigBuilder,
    InteractiveSessionStack session,
    InteractiveDiscoveryDependencies discovery,
    WorkingModeController modeController,
    ILogger<InteractiveBootstrapService> logger)
{
    /// <summary>
    /// Runs the bootstrap. Returns null if workspace trust was not granted.
    /// </summary>
    public async Task<InteractiveSession?> InitializeAsync(CancellationToken ct)
    {
        var startupResult = await startupFlowCoordinator.RunInteractiveAsync(ct).ConfigureAwait(false);
        if (!startupResult.ShouldContinue)
        {
            await Console.Error.WriteLineAsync("Workspace trust was not granted. Exiting interactive mode.");
            return null;
        }

        var systemPrompt = await promptConfigBuilder.BuildSystemPromptAsync(
            memoryQuery: null, ct).ConfigureAwait(false);

        var model = discovery.ConfigManager.Current.Effective.Model ?? string.Empty;

        HydrateAppStateFromConfig();

        await discovery.CommandRegistry.RefreshDynamicCommandsAsync(discovery.DynamicCommandSources, ct)
            .ConfigureAwait(false);
        var slashCommands = discovery.CommandRegistry.GetAll()
            .Select(c => new SlashCommandEntry(
                c.Name, c.Description,
                c.Source))
            .ToList();

        var initialMode = session.PermissionMode.CurrentMode == PermissionMode.Plan
            ? WorkingMode.Plan
            : WorkingMode.Build;
        modeController.Mode = initialMode;

        return new InteractiveSession(
            session.ConversationRunner, systemPrompt, session.SessionManager,
            modeController,
            null, slashCommands, model);
    }

    private void HydrateAppStateFromConfig()
    {
        var config = discovery.ConfigManager;
        var configEffortValue = EffortThinking.ParseEffort(
            config.Current.Effective.Get("effortValue", "medium"));

        session.AppState.Update(s => s with
        {
            MainLoopModel = config.Current.Effective.Model,
            ThinkingEnabled = config.Current.Effective.Get("thinkingEnabled", false),
            ShowThinking = config.Current.Effective.Get("showThinking", false),
            EffortValue = configEffortValue,
            Tools = discovery.ToolCatalog.Tools,
        });
    }
}
