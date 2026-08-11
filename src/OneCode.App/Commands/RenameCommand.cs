using OneCode.App.Session;

namespace OneCode.App.Commands;

public sealed class RenameCommand(ISessionManager sessionManager) : Command
{
    public override string Name => "rename";
    public override string Description => "Rename current session";
    public override CommandCategory Category => CommandCategory.Session;
    public override string? ArgumentHint => "<new-name>";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0)
            return CommandResult.Error("Usage: /rename <new-name>");

        var conv = sessionManager.ForegroundConversation;
        if (conv is null)
            return CommandResult.Error("No active session to rename.");

        var newName = string.Join(" ", args);
        conv.Name = newName;
        await sessionManager.SaveAsync(ct).ConfigureAwait(false);
        return CommandResult.Text($"Session renamed to: {newName}");
    }
}
