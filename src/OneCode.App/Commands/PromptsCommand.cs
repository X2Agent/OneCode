using System.Text;
using OneCode.Core.Prompt;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Abstractions;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Commands;

/// <summary>
/// /prompts — list and run saved <c>.prompt</c> templates from project/user prompt dirs
/// (same extension and layout as <see cref="PromptManager"/> / FilePromptStore).
/// </summary>
public sealed class PromptsCommand(
    IFileSystem fileSystem,
    IPromptManager promptManager) : Command
{
    private const string PromptExtension = ".prompt";

    public override string Name => "prompts";
    public override string Description => "Manage saved prompts";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ArgumentHint => "[list|run <name>]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0 || args[0] is "list" or "ls")
            return CommandResult.Text(ListPrompts());

        if (args[0] == "run" && args.Length > 1)
            return await RunPromptAsync(args[1], ct).ConfigureAwait(false);

        return CommandResult.Error("Usage: /prompts [list|run <name>]");
    }

    private string ListPrompts()
    {
        var dirs = GetPromptDirs();
        var sb = new StringBuilder("Prompts:\n");
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in fileSystem.FindFiles(dir, $"*{PromptExtension}"))
            {
                var relative = Path.GetRelativePath(dir, file)
                    .Replace('\\', '/')
                    .TrimStart('.', '/');
                if (relative.EndsWith(PromptExtension, StringComparison.OrdinalIgnoreCase))
                    relative = relative[..^PromptExtension.Length];
                if (!string.IsNullOrWhiteSpace(relative))
                    names.Add(relative);
            }
        }

        if (names.Count == 0)
            sb.AppendLine("  (none)");
        else
        {
            foreach (var name in names)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {name}");
        }

        sb.AppendLine("\nUse /prompts run <name> to execute a prompt (e.g. system/review).");
        return sb.ToString().TrimEnd();
    }

    private async Task<CommandResult> RunPromptAsync(string name, CancellationToken ct)
    {
        // Normalize path separators so "system\\review" and "system/review" both work.
        var normalized = name.Replace('\\', '/').Trim();
        if (normalized.EndsWith(PromptExtension, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^PromptExtension.Length];

        var content = await promptManager.GetPromptAsync(normalized, ct).ConfigureAwait(false);
        if (content is not null)
        {
            return CommandResult.Prompt(
                $"Execute the following prompt:\n\n{content}");
        }

        // Fallback: direct file read under project/user dirs (covers unsynced custom files).
        foreach (var dir in GetPromptDirs())
        {
            var path = Path.Combine(dir, normalized.Replace('/', Path.DirectorySeparatorChar) + PromptExtension);
            if (!PathsHelper.IsWithinDirectory(path, dir))
                return CommandResult.Error($"Invalid prompt name '{name}'.");

            content = await fileSystem.ReadTextFileAsync(path, ct).ConfigureAwait(false);
            if (content is not null)
            {
                return CommandResult.Prompt(
                    $"Execute the following prompt:\n\n{content}");
            }
        }

        return CommandResult.Error($"Prompt '{name}' not found.");
    }

    /// <summary>
    /// Prompt search directories in priority order: project, then user home
    /// (built-in prompts are reached via <see cref="PromptManager"/> on run).
    /// </summary>
    private static string[] GetPromptDirs()
    {
        var home = PathsHelper.UserHome;
        return
        [
            Path.Combine(Directory.GetCurrentDirectory(), Constants.App.ConfigDirName, "prompts"),
            Path.Combine(home, Constants.App.ConfigDirName, "prompts"),
        ];
    }
}
