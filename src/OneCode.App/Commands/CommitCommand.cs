using OneCode.Core.Prompt;

namespace OneCode.App.Commands;

/// <summary>
/// /commit — prompt command that generates git context for the AI agent to create a commit.
/// Prompt loaded from prompts/system/commit.prompt (overridable via project/user-level
/// .onecode/prompts/).
/// </summary>
public sealed class CommitCommand(
    IGitHelper gitHelper,
    IPromptManager promptManager) : Command
{
    public override string Name => "commit";
    public override string Description => "Create a git commit";
    public override CommandCategory Category => CommandCategory.Git;
    public override string? ProgressMessage => "creating commit";

    private static readonly string[] AllowedTools =
        ["Bash(git add:*)", "Bash(git status:*)", "Bash(git commit:*)", "Bash(git diff:*)"];

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var ctx = await GitContextSnapshot.ReadAsync(gitHelper, ct).ConfigureAwait(false);

        var variables = new Dictionary<string, string>
        {
            ["gitStatus"] = ctx.Status,
            ["gitDiff"] = ctx.Diff,
            ["gitBranch"] = ctx.Branch,
            ["gitLog"] = ctx.Log,
        };

        var prompt = await LoadPromptAsync(promptManager, "system/commit", variables, ct).ConfigureAwait(false);
        if (prompt is null)
            return CommandResult.Error("Prompt 'system/commit' is not available. Verify prompts/system/commit.prompt exists.");
        return CommandResult.Prompt(prompt, AllowedTools);
    }
}
