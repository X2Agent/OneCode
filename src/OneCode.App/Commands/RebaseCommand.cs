namespace OneCode.App.Commands;

/// <summary>
/// /rebase — directly execute git rebase onto a target branch.
///
/// Usage:
///   /rebase              → rebase onto main (default)
///   /rebase &lt;branch&gt;     → rebase onto the specified branch
///
/// If conflicts occur, git stops the rebase and reports conflicting files.
/// Use "git rebase --abort" to cancel, or resolve conflicts and "git rebase --continue".
/// This command does NOT push or force-push — that remains a manual step.
/// </summary>
public sealed class RebaseCommand(IGitHelper gitHelper) : Command
{
    public override string Name => "rebase";
    public override string Description => "Rebase current branch onto a target branch";
    public override CommandCategory Category => CommandCategory.Git;
    public override string? ArgumentHint => "[target-branch]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var target = args.Length > 0 ? args[0] : "main";

        var result = await gitHelper.RunAsync(["rebase", target], ct).ConfigureAwait(false);

        if (result is null)
            return CommandResult.Error("git is not available.");

        if (result.Success)
        {
            var output = result.Stdout.Trim();
            return CommandResult.Text(string.IsNullOrEmpty(output)
                ? $"Rebased onto {target}."
                : $"Rebased onto {target}.\n{output}");
        }

        // Rebase failure usually means conflicts or nothing to rebase.
        var err = result.Stderr.Trim();
        var combined = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(result.Stdout.Trim()))
            combined.AppendLine(result.Stdout.Trim());
        if (!string.IsNullOrEmpty(err))
            combined.Append(err);

        // Detect conflict state and offer recovery hint
        var hasConflicts = err.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase)
            || err.Contains("could not apply", StringComparison.OrdinalIgnoreCase);
        var hint = hasConflicts
            ? "\n\nConflicts detected. Resolve them, then run `git rebase --continue` (or `git rebase --abort` to cancel)."
            : "";

        return CommandResult.Error(
            $"git rebase {target} failed:{hint}\n{combined.ToString().TrimEnd()}");
    }
}
