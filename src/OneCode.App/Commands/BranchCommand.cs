namespace OneCode.App.Commands;

public sealed class BranchCommand(IGitHelper gitHelper) : Command
{
    public override string Name => "branch";
    public override string Description => "Create or switch git branches";
    public override CommandCategory Category => CommandCategory.Git;
    public override string? ArgumentHint => "[branch-name]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0)
        {
            var branches = await gitHelper.RunAsync(["branch"], ct).ConfigureAwait(false);
            return branches is null
                ? CommandResult.Error("git is not available.")
                : branches.Success
                    ? CommandResult.Text($"Git branches:\n{GitHelper.FormatOutput(branches)}")
                    : CommandResult.Error($"Failed to list branches:\n{GitHelper.FormatFailure(branches)}");
        }

        var branchName = args[0].Trim();
        if (string.IsNullOrWhiteSpace(branchName))
        {
            return CommandResult.Error("Branch name is required.");
        }

        var switchResult = await gitHelper.RunAsync(["switch", branchName], ct).ConfigureAwait(false);
        if (switchResult is null)
        {
            return CommandResult.Error("git is not available.");
        }

        if (switchResult.Success)
        {
            return CommandResult.Text($"Switched to branch: {branchName}\n{GitHelper.FormatOutput(switchResult)}");
        }

        var createResult = await gitHelper.RunAsync(["switch", "-c", branchName], ct).ConfigureAwait(false);
        return createResult is null
            ? CommandResult.Error("git is not available.")
            : createResult.Success
                ? CommandResult.Text($"Created and switched to branch: {branchName}\n{GitHelper.FormatOutput(createResult)}")
                : CommandResult.Error($"Failed to switch or create branch '{branchName}':\n{GitHelper.FormatFailure(createResult)}");
    }
}
