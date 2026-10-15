using OneCode.App.Session;

namespace OneCode.App.Commands;

/// <summary>
/// /close — close a session. Without arguments closes the current foreground
/// session (the next message lazily starts a fresh one); with an id prefix,
/// resolves it to a session and closes it whether it is the foreground or a
/// background session.
/// </summary>
public sealed class CloseCommand(ISessionManager sessionManager) : Command
{
    public override string Name => "close";
    public override string Description => "Close a session (default: current)";
    public override CommandCategory Category => CommandCategory.Session;
    public override bool Immediate => true;
    public override string? ArgumentHint => "[session-id-prefix]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0)
        {
            if (sessionManager.ForegroundConversation is null)
                return CommandResult.Error("No active session to close.");

            var foregroundId = sessionManager.ForegroundConversation.Id.ToString();
            await sessionManager.CloseAsync(ct).ConfigureAwait(false);
            return CommandResult.Text($"Closed session {foregroundId}.");
        }

        var sessions = await sessionManager.ListAsync(ct).ConfigureAwait(false);
        var resolution = SessionIdResolver.Resolve(args[0], sessions);
        if (resolution.Error is not null)
            return CommandResult.Error(resolution.Error);
        if (resolution.Candidates.Count > 0)
            return CommandResult.Error(SessionIdResolver.FormatAmbiguousError(resolution.Candidates));

        var targetId = resolution.Id!.Value.ToString();
        if (sessionManager.ForegroundConversation?.Id.ToString() == targetId)
        {
            await sessionManager.CloseAsync(ct).ConfigureAwait(false);
            return CommandResult.Text($"Closed session {targetId}.");
        }

        var closed = await sessionManager.CloseBackgroundSessionAsync(targetId, ct)
            .ConfigureAwait(false);
        return closed
            ? CommandResult.Text($"Closed background session {targetId}.")
            : CommandResult.Error($"Session '{args[0]}' not found.");
    }
}
