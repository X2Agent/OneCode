using OneCode.Core.Commands;

namespace OneCode.App.Services;

/// <summary>
/// Discriminated result from <see cref="SlashCommandPipeline.TryResolvePromptCommandAsync"/>
/// indicating how the TUI dispatch layer should handle the command.
/// </summary>
public abstract record CommandDispatchResult
{
    /// <summary>Command produced a prompt to be streamed through the LLM pipeline.</summary>
    public sealed record Prompt(string Content, string[]? AllowedTools) : CommandDispatchResult;

    /// <summary>Command requests resuming a durable workflow — route directly to the workflow resume stream.</summary>
    public sealed record ResumeWorkflow(string SessionId, WorkflowResumeKind Kind) : CommandDispatchResult;
}

/// <summary>
/// Handles slash-command resolution and execution. Owns the
/// <see cref="CommandExecutionState"/> that caches non-prompt results,
/// plus the exit-requested flag consulted by the TUI lifecycle.
/// </summary>
/// <remarks>
/// Extracted from <see cref="InteractiveModeExecutor"/> to keep that class
/// focused on orchestration. The <see cref="CommandState"/> property is shared
/// with <see cref="TuiHostConfigurator"/> (for session-UI refresh wiring).
/// </remarks>
public sealed class SlashCommandPipeline(
    ICommandRegistry commandRegistry)
{
    private readonly CommandExecutionState _cmdState = new();
    private volatile bool _exitRequested;

    /// <summary>True after an ExitResult command (e.g. /exit) was executed.</summary>
    public bool IsExitRequested => _exitRequested;

    /// <summary>Shared mutable state for the slash-command pipeline.</summary>
    public CommandExecutionState CommandState => _cmdState;

    /// <summary>
    /// Executes a slash command and checks if it produced a PromptResult or ResumeWorkflowResult.
    /// Returns a <see cref="CommandDispatchResult"/> indicating how the dispatch layer should proceed.
    /// If neither, caches the result for <see cref="ExecuteCommandAsync"/> to consume.
    /// </summary>
    public async Task<CommandDispatchResult?> TryResolvePromptCommandAsync(
        InteractiveSession session, string text, CancellationToken ct)
    {
        var cmd = commandRegistry.Find(text);
        if (cmd is null) return null;

        var result = await ExecuteFoundCommandAsync(cmd, text, ct).ConfigureAwait(false);

        if (result is CommandResult.PromptResult pr)
            return new CommandDispatchResult.Prompt(pr.Content, pr.AllowedTools);

        if (result is CommandResult.ResumeWorkflowResult rw)
            return new CommandDispatchResult.ResumeWorkflow(rw.SessionId, rw.Kind);

        _cmdState.CacheResult(result);
        return null;
    }

    /// <summary>
    /// Executes a slash command. If <see cref="TryResolvePromptCommandAsync"/>
    /// already executed and cached the result (non-PromptResult), returns it
    /// without re-execution. Otherwise executes the command and maps the result.
    /// </summary>
    public async Task<string?> ExecuteCommandAsync(
        InteractiveSession session, string text, CancellationToken ct)
    {
        if (_cmdState.ConsumeCachedResult() is { } cached)
            return MapResultToDisplay(cached);

        var cmd = commandRegistry.Find(text);
        if (cmd is null)
        {
            var name = text.TrimStart('/').Split(' ')[0];
            return commandRegistry.Suggest(text) ?? $"Unknown command '/{name}'. Type /help to see available commands.";
        }

        var result = await ExecuteFoundCommandAsync(cmd, text, ct).ConfigureAwait(false);
        return MapResultToDisplay(result);
    }

    private async Task<CommandResult> ExecuteFoundCommandAsync(
        ICommand cmd, string text, CancellationToken ct)
    {
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var args = parts.Length > 1 ? parts[1..] : Array.Empty<string>();

        var result = await cmd.ExecuteAsync(args, ct).ConfigureAwait(false);

        if (result is CommandResult.ExitResult)
            _exitRequested = true;

        if (result is not CommandResult.ErrorResult && cmd.Name == "session")
            await _cmdState.RefreshSessionUiAsync(ct).ConfigureAwait(false);

        return result;
    }

    private static string? MapResultToDisplay(CommandResult result) => result switch
    {
        CommandResult.TextResult t => t.Value,
        CommandResult.ErrorResult e => e.Message,
        CommandResult.ExitResult => "Goodbye!",
        CommandResult.SilentResult => null,
        _ => null,
    };
}
