using OneCode.App.Session;

namespace OneCode.App.Commands;

/// <summary>
/// /new — start a new session, backgrounding the current one (it can be
/// resumed later via /session switch). Optional trailing words become the
/// new session's name.
/// </summary>
public sealed class NewCommand(ISessionManager sessionManager) : Command
{
    public override string Name => "new";
    public override string Description => "Start a new session (backgrounds current)";
    public override CommandCategory Category => CommandCategory.Session;
    public override bool Immediate => true;
    public override string? ArgumentHint => "[name]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var name = args.Length > 0 ? string.Join(" ", args) : null;
        var cwd = sessionManager.ForegroundConversation?.WorkingDirectory
                  ?? Directory.GetCurrentDirectory();

        var conv = await sessionManager.BackgroundCurrentAndCreateNewAsync(
            new ConversationOptions(cwd, Name: name), ct).ConfigureAwait(false);

        return CommandResult.Text($"New session created: {conv.Id}\n  Name: {conv.Name}");
    }
}
