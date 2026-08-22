using OneCode.App.Session;
using System.Text;

namespace OneCode.App.Commands;

/// <summary>
/// /session — session lifecycle only (list / new / switch / close).
/// Runtime diagnostics (model, permissions, thinking, tokens) live on <c>/status</c>.
/// In TUI, bare <c>/session</c> opens the resume chooser (handled before this command runs).
/// </summary>
public sealed class SessionCommand(ISessionManager sessionManager) : Command
{
    public override string Name => "session";
    public override string Description => "Manage session lifecycle (list, new, switch, close)";
    public override CommandCategory Category => CommandCategory.Session;
    public override bool Immediate => true;
    public override string? ArgumentHint => "[list|new|switch|close]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0)
            return ShowUsage();

        return args[0].ToLowerInvariant() switch
        {
            // Former /session info overlapped /status; keep a redirect for muscle memory.
            "info" => CommandResult.Text(
                "Session identity and runtime diagnostics moved to /status.\n" +
                "Use /session list|new|switch|close for lifecycle."),
            "list" or "ls" => await ListSessionsAsync(ct),
            "new" => await NewSessionAsync(args.Skip(1).ToArray(), ct),
            "switch" => await SwitchSessionAsync(args.Skip(1).ToArray(), ct),
            "close" => await CloseSessionAsync(args.Skip(1).ToArray(), ct),
            _ => CommandResult.Error($"Unknown session command: {args[0]}. Use: list, new, switch, close")
        };
    }

    private static CommandResult ShowUsage() =>
        CommandResult.Text("""
            Session lifecycle:
              /session list              List sessions
              /session new [name]        Create a new session (backgrounds current)
              /session switch <id>       Switch to a session
              /session close <id>        Close a session

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

    private async Task<CommandResult> NewSessionAsync(string[] args, CancellationToken ct)
    {
        var name = args.Length > 0 ? string.Join(" ", args) : null;
        var cwd = sessionManager.ForegroundConversation?.WorkingDirectory
                  ?? Directory.GetCurrentDirectory();

        var conv = await sessionManager.BackgroundCurrentAndCreateNewAsync(
            new ConversationOptions(cwd, Name: name), ct).ConfigureAwait(false);

        return CommandResult.Text($"New session created: {conv.Id}\n  Name: {conv.Name}");
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

    private async Task<CommandResult> CloseSessionAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
            return CommandResult.Error("Usage: /session close <session-id>");

        if (sessionManager.ForegroundConversation?.Id.ToString() == args[0])
        {
            await sessionManager.CloseAsync(ct).ConfigureAwait(false);
            return CommandResult.Text($"Closed foreground session {args[0]}.");
        }

        var closed = await sessionManager.CloseBackgroundSessionAsync(args[0], ct).ConfigureAwait(false);
        return closed
            ? CommandResult.Text($"Closed background session {args[0]}.")
            : CommandResult.Error($"Background session '{args[0]}' not found.");
    }
}
