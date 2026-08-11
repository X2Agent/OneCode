using Microsoft.Extensions.Hosting;
using OneCode.App.Session;

namespace OneCode.App.Commands;

public sealed class ExitCommand(ISessionManager sessionManager, IHostApplicationLifetime lifetime, ILogger<ExitCommand>? logger = null) : Command
{
    public override string Name => "exit";
    public override string Description => "Exit One Code";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override IReadOnlyList<string> Aliases => ["quit"];

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (sessionManager.ForegroundConversation is not null)
        {
            try { await sessionManager.SaveAsync(ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                if (logger is not null)
                    logger.LogDebug(ex, "ExitCommand: best-effort save on exit failed");
                else
                    System.Diagnostics.Debug.WriteLine($"ExitCommand best-effort save on exit failed: {ex.Message}");
            }
        }

        lifetime.StopApplication();
        return CommandResult.Exit();
    }
}
