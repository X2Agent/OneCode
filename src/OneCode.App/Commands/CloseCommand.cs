using OneCode.App.Session;

namespace OneCode.App.Commands;

/// <summary>
/// /close — close a session. Without arguments closes the current foreground
/// session (the next message lazily starts a fresh one); with a session id,
/// closes that session whether it is the foreground or a background session.
/// </summary>
public sealed class CloseCommand(ISessionManager sessionManager) : Command
{
    public override string Name => "close";
    public override string Description => "Close a session (default: current)";
    public override CommandCategory Category => CommandCategory.Session;
    public override bool Immediate => true;
    public override string? ArgumentHint => "[session-id]";

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

        if (sessionManager.ForegroundConversation?.Id.ToString() == args[0])
        {
            await sessionManager.CloseAsync(ct).ConfigureAwait(false);
            return CommandResult.Text($"Closed session {args[0]}.");
        }

        var closed = await sessionManager.CloseBackgroundSessionAsync(args[0], ct)
            .ConfigureAwait(false);
        return closed
            ? CommandResult.Text($"Closed background session {args[0]}.")
            : CommandResult.Error($"Session '{args[0]}' not found.");
    }
}
