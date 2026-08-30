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
            // A7：用户输入退出 → SessionEnd(reason=prompt_input_exit)；CloseAsync 内含 best-effort 持久化
            try { await sessionManager.CloseAsync(SessionEndReason.PromptInputExit, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                if (logger is not null)
                    logger.LogDebug(ex, "ExitCommand: best-effort close on exit failed");
                else
                    System.Diagnostics.Debug.WriteLine($"ExitCommand best-effort close on exit failed: {ex.Message}");
            }
        }

        lifetime.StopApplication();
        return CommandResult.Exit();
    }
}
