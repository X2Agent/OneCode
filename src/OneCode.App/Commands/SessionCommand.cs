using OneCode.Core.Session;
using OneCode.App.Session;
using System.Text;

namespace OneCode.App.Commands;

/// <summary>
/// /session — session lifecycle navigation (list / switch / delete).
/// Creation lives on <c>/new</c> (the <c>new</c> subcommand delegates to it for
/// grouped access); closing lives on <c>/close</c>.
/// Runtime diagnostics live on <c>/status</c>.
/// In TUI, bare <c>/session</c> opens the resume chooser (handled before this command runs).
/// </summary>
public sealed class SessionCommand(
    ISessionManager sessionManager,
    ISessionEventStore eventStore,
    NewCommand newCommand) : Command
{
    public override string Name => "session";
    public override string Description => "Manage session lifecycle (list, new, switch, delete)";
    public override CommandCategory Category => CommandCategory.Session;
    public override bool Immediate => true;
    public override string? ArgumentHint => "[list|new|switch|delete]";

    public override Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0)
            return Task.FromResult(ShowUsage());

        return args[0].ToLowerInvariant() switch
        {
            "list" or "ls" => ListSessionsAsync(ct),
            "new" => newCommand.ExecuteAsync(args.Skip(1).ToArray(), ct),
            "switch" => SwitchSessionAsync(args.Skip(1).ToArray(), ct),
            "delete" => DeleteSessionAsync(args.Skip(1).ToArray(), ct),
            _ => Task.FromResult(CommandResult.Error(
                $"Unknown session command: {args[0]}. Use: list, new, switch, delete"))
        };
    }

    private static CommandResult ShowUsage() =>
        CommandResult.Text($$"""
            Session lifecycle:
              /session                        Open the resume chooser
              /session list                   List sessions (short ids)
              /session new [name]             Create a new session (backgrounds current)
              /session switch <id-prefix>     Switch to a session
              /session delete <id-prefix> [--force]   Delete a session from disk

            Shortcuts: /new [name], /close <id-prefix>
            switch/close/delete accept any unique id prefix (e.g. 4f).
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
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {SessionIdResolver.ShortId(s.Id)}{active}{background}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"    {s.Title ?? "(未命名)"}  ({mode}, {s.MessageCount} msgs, {s.LastActivityAt:yyyy-MM-dd HH:mm})");
        }
        if (sessions.Count > 20)
            sb.AppendLine(CultureInfo.InvariantCulture, $"  ... and {sessions.Count - 20} more. Use /session switch <id-prefix> to switch.");
        return CommandResult.Text(sb.ToString().TrimEnd());
    }

    private async Task<CommandResult> SwitchSessionAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
            return CommandResult.Error("Usage: /session switch <session-id-prefix>");

        var sessions = await sessionManager.ListAsync(ct).ConfigureAwait(false);
        var resolution = SessionIdResolver.Resolve(args[0], sessions);
        if (resolution.Error is not null)
            return CommandResult.Error(resolution.Error);
        if (resolution.Candidates.Count > 0)
            return CommandResult.Error(SessionIdResolver.FormatAmbiguousError(resolution.Candidates));

        var conv = await sessionManager.SwitchToSessionAsync(resolution.Id!.Value, ct).ConfigureAwait(false);
        return conv is null
            ? CommandResult.Error($"Session '{args[0]}' not found.")
            : CommandResult.Text($"Switched to {SessionIdResolver.ShortId(conv.Id)} ({conv.Messages.Count} msgs)");
    }

    private async Task<CommandResult> DeleteSessionAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
            return CommandResult.Error("Usage: /session delete <session-id-prefix> [--force]");

        var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
        var idInput = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
            ?? string.Empty;

        var sessions = await sessionManager.ListAsync(ct).ConfigureAwait(false);
        var resolution = SessionIdResolver.Resolve(idInput, sessions);
        if (resolution.Error is not null)
            return CommandResult.Error(resolution.Error);
        if (resolution.Candidates.Count > 0)
            return CommandResult.Error(SessionIdResolver.FormatAmbiguousError(resolution.Candidates));

        var target = resolution.Id!.Value;

        // 内存态会话仍在写回事件日志，直接删文件会被下一次写回复活——先关再删。
        var foregroundId = sessionManager.ForegroundConversation?.Id;
        if (foregroundId == target)
            return CommandResult.Error("Cannot delete the active foreground session. Close it first (/close), then delete.");
        if (sessionManager.BackgroundSessions.Any(b => b.Conversation.Id == target))
            return CommandResult.Error("Cannot delete a background session. Close it first (/close <id>), then delete.");

        var summary = sessions.FirstOrDefault(s => s.Id == target);
        if (!force)
        {
            var title = summary?.Title ?? "(未命名)";
            var lastActivity = summary?.LastActivityAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "unknown";
            return CommandResult.Text(
                $"'{title}' ({SessionIdResolver.ShortId(target)}, last active {lastActivity}) will be deleted permanently. " +
                $"Run /session delete {idInput} --force to confirm.");
        }

        await eventStore.DeleteAsync(target, ct).ConfigureAwait(false);
        return CommandResult.Text($"Deleted session {SessionIdResolver.ShortId(target)} ({summary?.Title ?? "未命名"}).");
    }
}
