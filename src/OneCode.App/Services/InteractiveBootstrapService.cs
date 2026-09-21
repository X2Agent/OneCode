using OneCode.App.Services.Mcp;
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
    McpStartupPreconnector mcpPreconnector,
    InteractiveSessionStack session,
    InteractiveDiscoveryDependencies discovery,
    WorkingModeController modeController)
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

        // MCP 预连接后台并发执行（Plan B）：不再阻塞系统提示词构建与 TUI 首屏渲染。
        // 无需在完成后补挂技能：skills provider 每次 agent run 构建，
        // 预连接期间缺席的 MCP skills 会在其连上后自动出现。
        mcpPreconnector.StartBackground(onCompleted: null, ct: ct);

        var systemPrompt = await promptConfigBuilder.BuildSystemPromptAsync(
            ct).ConfigureAwait(false);

        // The harness fragment travels with the body: MAF composes the two halves at agent build time,
        // so both must be loaded once per session and handed over separately. Missing file throws here
        // rather than degrading to MAF's generic default instructions.
        var harnessPrompt = await promptConfigBuilder.LoadHarnessAsync(ct).ConfigureAwait(false);

        var model = discovery.ConfigManager.Current.Effective.Model ?? string.Empty;

        HydrateAppStateFromConfig();

        await discovery.CommandRegistry.RefreshDynamicCommandsAsync(discovery.DynamicCommandSources, ct)
            .ConfigureAwait(false);
        var slashCommands = discovery.CommandRegistry.GetAll()
            .Select(c => new SlashCommandEntry(
                c.Name, c.Description,
                c.Source, c.ArgumentHint))
            .ToList();

        var initialMode = session.PermissionMode.CurrentMode == PermissionMode.Plan
            ? WorkingMode.Plan
            : WorkingMode.Build;
        modeController.Mode = initialMode;

        return new InteractiveSession(
            session.ConversationRunner, systemPrompt, session.SessionManager,
            modeController,
            null, slashCommands, model, harnessPrompt);
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
