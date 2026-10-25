using OneCode.App.Query;
using OneCode.App.Session;

using OneCode.App.Commands;

namespace OneCode.App.Services;

/// <summary>
/// Runtime artifacts produced by <c>InteractiveBootstrapService.InitializeAsync</c>
/// and consumed by both the executor's streaming/command methods and
/// <c>TuiHostConfigurator</c>. Carries the shared, immutable session
/// state needed to wire the TUI and dispatch queries.
/// </summary>
/// <remarks>
/// <see cref="SystemPrompt"/> is the agent body (<c>system/default</c> or role prompt) and
/// <see cref="HarnessPrompt"/> the shared harness fragment (<c>system/harness</c>). They stay
/// separate because MAF composes them via <c>HarnessInstructions</c> (harness first) and
/// <c>ChatOptions.Instructions</c> (body second). Both are loaded once per session, together.
/// </remarks>
public sealed record InteractiveSession(
    IConversationRunner ConversationRunner,
    string SystemPrompt,
    ISessionManager SessionManager,
    WorkingModeController ModeController,
    IReadOnlyList<SlashCommandEntry> SlashCommands,
    string HarnessPrompt);
