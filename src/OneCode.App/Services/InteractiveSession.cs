using OneCode.App.Query;
using OneCode.App.Session;
using OneCode.App.Tui;

namespace OneCode.App.Services;

/// <summary>
/// Runtime artifacts produced by <see cref="InteractiveModeExecutor.InitializeAsync"/>
/// and consumed by both the executor's streaming/command methods and
/// <see cref="TuiHostConfigurator"/>. Carries the shared, immutable session
/// state needed to wire the TUI and dispatch queries.
/// </summary>
public sealed record InteractiveSession(
    IConversationRunner ConversationRunner,
    string SystemPrompt,
    ISessionManager SessionManager,
    WorkingModeController ModeController,
    string? SshHost,
    IReadOnlyList<SlashCommandEntry> SlashCommands,
    string Model);
