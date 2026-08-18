using OneCode.App.Tui;
using OneCode.Core.Keybindings;
using OneCode.Core.Product;
using OneCode.Core.IO;
using OneCode.Core.Coordinator;

namespace OneCode.App.Services;

/// <summary>
/// Builds <see cref="TuiContext"/> for a single interactive run.
/// Must be invoked per session — closures capture the live <see cref="InteractiveSession"/>.
/// </summary>
public sealed class TuiContextFactory(
    TuiStreamingDependencies streaming,
    TuiCatalogDependencies catalog,
    TuiInteractionBridge tuiInteractionBridge,
    InteractiveTuiDependencies tuiDeps,
    OneCode.Core.Lsp.ILspServerManager lspServerManager,
    Services.Lsp.LspDiagnosticRegistry lspDiagnosticRegistry,
    IClipboardService clipboard,
    ITeamOrchestrationService teamOrchestrationService,
    OneCode.Core.Commands.IGitHelper gitHelper)
{
    public TuiContext Create(
        InteractiveSession session,
        KeybindingResolver keyResolver,
        KeybindingContextManager keyContextManager,
        string? initialPrompt,
        out Action<Action<TuiEvent>> emitEventBinder,
        CancellationToken ct)
    {
        Action<TuiEvent>? emitEvent = null;
        emitEventBinder = dispatch =>
        {
            emitEvent = dispatch;
            tuiInteractionBridge.SetEmitter(emitEvent);
        };

        var query = new TuiQueryServices(
            StreamQuery: (text, imagePaths, token) => streaming.QueryStream.StreamQueryAsync(session, text, imagePaths, token),
            CreateSession: async token =>
            {
                var currentModelId = catalog.AppState.Current.MainLoopModel ?? string.Empty;
                await session.SessionManager.EnsureActiveSessionAsync(
                    new ConversationOptions(Environment.CurrentDirectory, currentModelId), token)
                    .ConfigureAwait(false);
            },
            ExecuteCommand: (text, token) => streaming.SlashCommands.ExecuteCommandAsync(session, text, token),
            IsExitRequested: () => streaming.SlashCommands.IsExitRequested,
            IsImmediateCommand: input => catalog.CommandRegistry.Find(input) is { Immediate: true },
            GetProgressMessage: input => catalog.CommandRegistry.Find(input)?.ProgressMessage,
            TryResolvePromptCommand: (text, token) => streaming.SlashCommands.TryResolvePromptCommandAsync(session, text, token),
            StreamCommandPrompt: (prompt, tools, token) => streaming.QueryStream.StreamCommandPromptAsync(session, prompt, tools, token),
            StreamResumeWorkflow: (sessionId, kind, token) => streaming.QueryStream.StreamResumeWorkflowAsync(session, sessionId, kind, token),
            InputQueue: streaming.InputQueue,
            ReplayCurrentBuildRun: async token =>
            {
                var conversation = session.SessionManager.ForegroundConversation;
                return conversation is null
                    ? null
                    : await tuiDeps.BuildRunTuiReplay.ReplayLatestAsync(conversation.Id, token)
                        .ConfigureAwait(false);
            });

        var sessionServices = new TuiSessionServices(
            GetSessionUserPrompts: () =>
            {
                var conversation = session.SessionManager.ForegroundConversation;
                if (conversation is null) return [];
                return conversation.Messages
                    .OfType<UserMessage>()
                    .Where(um => !um.IsMeta && !string.IsNullOrWhiteSpace(um.Content))
                    .Select(um => um.Content)
                    .ToList();
            },
            GetSessionName: () =>
            {
                var conversation = session.SessionManager.ForegroundConversation;
                if (conversation is null) return null;
                if (!string.IsNullOrWhiteSpace(conversation.Name))
                    return conversation.Name;
                var id = conversation.Id.Value;
                return id.Length <= 8 ? id : id[..8];
            },
            GetActiveTeam: () => teamOrchestrationService.ResolveActiveTeam(),
            GetRegisteredTeams: () => teamOrchestrationService.RegisteredTeams,
            CycleTeam: () =>
            {
                var teams = teamOrchestrationService.RegisteredTeams;
                if (teams.Count == 0) return null;
                var current = teamOrchestrationService.ResolveActiveTeam();
                var nextTeam = teams.FirstOrDefault(t =>
                    string.Equals(t, current, StringComparison.OrdinalIgnoreCase)) is { } cur
                        ? teams[(teams.IndexOf(cur) + 1) % teams.Count]
                        : teams[0];
                teamOrchestrationService.ActiveTeam = nextTeam;
                return nextTeam;
            },
            GitHelper: gitHelper);

        var diagnostics = new TuiDiagnosticServices(
            GetLspServerStatus: () => lspServerManager.GetStatus(),
            GetLspDiagnostics: () => lspDiagnosticRegistry.GetAllDiagnostics(),
            SubscribeDiagnosticsChanged: handler => lspDiagnosticRegistry.DiagnosticsChanged += handler,
            UnsubscribeDiagnosticsChanged: handler => lspDiagnosticRegistry.DiagnosticsChanged -= handler);

        var runtime = new TuiRuntimeServices(
            Model: session.Model,
            ModelCatalog: catalog.ModelCatalog,
            ModeController: session.ModeController,
            KeyResolver: keyResolver,
            KeyContextManager: keyContextManager,
            TrustService: tuiDeps.TrustService,
            ImagePipeline: tuiDeps.ImagePipeline,
            RecordCost: () => tuiDeps.CostTracker.FormatCost(),
            Clipboard: clipboard,
            GetToolNames: () => catalog.ToolCatalog.GetVisibleToolNames(),
            GetShowThinking: () => catalog.AppState.Current.ShowThinking,
            EmitEvent: evt => emitEvent?.Invoke(evt));

        var options = new TuiLaunchOptions(
            Version: ProductInfo.Default.Version,
            ExternalCancellation: ct,
            SlashCommands: session.SlashCommands,
            SshHost: session.SshHost,
            InitialPrompt: initialPrompt);

        return new TuiContext(query, sessionServices, diagnostics, runtime, options);
    }
}
