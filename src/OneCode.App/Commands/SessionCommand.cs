using OneCode.App.Session;
using System.Text;

namespace OneCode.App.Commands;

/// <summary>
/// /session — session lifecycle navigation (list / switch).
/// Creation and closing live on <c>/new</c> and <c>/close</c>; the
/// <c>new</c>/<c>close</c> subcommands delegate to them for grouped access.
/// Runtime diagnostics live on <c>/status</c>.
/// In TUI, bare <c>/session</c> opens the resume chooser (handled before this command runs).
/// </summary>
public sealed class SessionCommand(
    ISessionManager sessionManager,
    NewCommand newCommand,
    CloseCommand closeCommand) : Command
{
    public override string Name => "session";
    public override string Description => "Manage session lifecycle (list, new, switch, close)";
    public override CommandCategory Category => CommandCategory.Session;
    public override bool Immediate => true;
    public override string? ArgumentHint => "[list|new|switch|close]";

    public override Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0)
            return Task.FromResult(ShowUsage());

        return args[0].ToLowerInvariant() switch
        {
            "list" or "ls" => ListSessionsAsync(ct),
            "new" => newCommand.ExecuteAsync(args.Skip(1).ToArray(), ct),
            "switch" => SwitchSessionAsync(args.Skip(1).ToArray(), ct),
            "close" => closeCommand.ExecuteAsync(args.Skip(1).ToArray(), ct),
            _ => Task.FromResult(CommandResult.Error(
                $"Unknown session command: {args[0]}. Use: list, new, switch, close"))
        };
    }

    private static CommandResult ShowUsage() =>
        CommandResult.Text("""
            Session lifecycle:
              /session list              List sessions
              /session new [name]        Create a new session (backgrounds current)
              /session switch <id>       Switch to a session
              /session close [id]        Close a session (default: current)

            Shortcuts: /new [name], /close [id]
            Runtime status (model, permissions, thinking, tokens): /status
            """);

    private async Task<CommandResult> ListSessionsAsync(CancellationToken ct)
    {
        var sessions = await sessionManager.ListAsync(ct).ConfigureAwait(false);
        if (sessions.Count == 0)
            return CommandResult.Text("No sessions found.");

        var sb = new StringBuilder($"Sessions ({sessions.Count}):\n");
        foreach (var s in sessions.Take(20))
        {
            var active = sessionManager.ForegroundConversation?.Id == s.Id ? " *" : "";
            var background = sessionManager.BackgroundSessions.Any(b => b.Conversation.Id == s.Id) ? " [bg]" : "";
            var mode = s.Mode ?? "build";
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {s.Id}{active}{background}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    {s.Name}  ({mode}, {s.MessageCount} msgs, {s.LastActivityAt:yyyy-MM-dd HH:mm})");
        }
        if (sessions.Count > 20)
            sb.AppendLine(CultureInfo.InvariantCulture, $"  ... and {sessions.Count - 20} more. Use /session switch <id> to switch.");
        return CommandResult.Text(sb.ToString().TrimEnd());
    }

    private async Task<CommandResult> SwitchSessionAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
            return CommandResult.Error("Usage: /session switch <session-id>");

        var conv = await sessionManager.SwitchToSessionAsync(args[0], ct).ConfigureAwait(false);
        return conv is null
            ? CommandResult.Error($"Session '{args[0]}' not found.")
            : CommandResult.Text($"Switched to {conv.Id} ({conv.Messages.Count} msgs)");
    }
}
