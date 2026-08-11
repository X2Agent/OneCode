namespace OneCode.App.Commands;

/// <summary>
/// /stash — directly execute git stash operations.
///
/// Subcommands:
///   /stash                  → list stash entries (default)
///   /stash list             → list stash entries
///   /stash push [message]   → stash current changes (with optional message)
///   /stash pop [n]          → pop stash@{n} (default: stash@{0})
///   /stash drop [n]         → drop stash@{n} (default: stash@{0})
/// </summary>
public sealed class StashCommand(IGitHelper gitHelper) : Command
{
    public override string Name => "stash";
    public override string Description => "Manage git stash (push, pop, list, drop)";
    public override CommandCategory Category => CommandCategory.Git;
    public override string? ArgumentHint => "[list|push [msg]|pop [n]|drop [n]]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";

        return sub switch
        {
            "list" or "ls" => await RunAsync(["stash", "list"], ct),
            "push" or "save" => await PushAsync(args[1..], ct),
            "pop" => await PopAsync(args[1..], ct),
            "drop" => await DropAsync(args[1..], ct),
            _ => CommandResult.Error($"Unknown stash subcommand: {sub}. Use: list, push, pop, drop"),
        };
    }

    private async Task<CommandResult> PushAsync(string[] args, CancellationToken ct)
    {
        var message = args.Length > 0 ? string.Join(" ", args) : null;
        string[] gitArgs = message is null
            ? ["stash", "push"]
            : ["stash", "push", "-m", message];
        return await RunAsync(gitArgs, ct);
    }

    private async Task<CommandResult> PopAsync(string[] args, CancellationToken ct)
    {
        var index = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 0;
        return await RunAsync(["stash", "pop", $"stash@{{{index}}}"], ct);
    }

    private async Task<CommandResult> DropAsync(string[] args, CancellationToken ct)
    {
        var index = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 0;
        return await RunAsync(["stash", "drop", $"stash@{{{index}}}"], ct);
    }

    private async Task<CommandResult> RunAsync(string[] gitArgs, CancellationToken ct)
    {
        var result = await gitHelper.RunAsync(gitArgs, ct).ConfigureAwait(false);
        if (result is null)
            return CommandResult.Error("git is not available.");

        if (result.Success)
        {
            var output = result.Stdout.Trim();
            return CommandResult.Text(string.IsNullOrEmpty(output) ? "Done." : output);
        }

        var err = result.Stderr.Trim();
        return CommandResult.Error(string.IsNullOrEmpty(err) ? "git stash failed." : err);
    }
}
