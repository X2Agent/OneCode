using System.Text;
using OneCode.Core.Skills;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Commands;

/// <summary>
/// /skills — list and inspect skills. Execution is via the dynamic slash command
/// <c>/&lt;skillname&gt;</c> registered by <see cref="SkillCommandSource"/>.
/// </summary>
public sealed class SkillsCommand : Command
{
    public override string Name => "skills";
    public override string Description => "List or inspect skills (run via /<skillname>)";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ArgumentHint => "[list|show <name>]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0 || args[0] is "list" or "ls")
            return CommandResult.Text(await ListSkillsAsync(ct).ConfigureAwait(false));

        if (args[0] == "show" && args.Length > 1)
            return CommandResult.Text(await ShowSkillAsync(args[1], ct).ConfigureAwait(false));

        // Bare /skills <name> → show (matches docs/skills.md discovery table).
        if (args.Length == 1 && args[0] is not ("list" or "ls" or "show" or "run"))
            return CommandResult.Text(await ShowSkillAsync(args[0], ct).ConfigureAwait(false));

        if (args[0] == "run")
        {
            var skillName = args.Length > 1 ? args[1] : "<name>";
            return CommandResult.Error(
                $"'/skills run' was removed. Execute skills directly: /{skillName} [args]\n" +
                "Use /skills list to browse, /skills show <name> to preview.");
        }

        return CommandResult.Error("Usage: /skills [list|show <name>] — run a skill with /<skillname>");
    }

    private static async Task<string> ListSkillsAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();

        // Bundled skills
        var bundled = BundledSkills.All;
        if (bundled.Count > 0)
        {
            sb.AppendLine("Bundled Skills:");
            foreach (var (name, skill) in bundled.OrderBy(kv => kv.Key))
                sb.AppendLine(CultureInfo.InvariantCulture, $"  /{name,-24} {skill.Description}");
        }

        // Filesystem skills
        var dirs = GetSkillDirectories();
        var found = false;
        var fsSection = new StringBuilder();
        // De-dup by skill name across candidate config dirs (.onecode → .agent → .claude);
        // first occurrence wins because dirs are enumerated in priority order.
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;

            // Directory-format skills (name/SKILL.md)
            foreach (var skillDir in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(skillDir);
                if (bundled.ContainsKey(name)) continue; // skip shadowed bundled skills
                if (!emitted.Add(name)) continue;        // already listed from a higher-priority dir
                var mdFile = Path.Combine(skillDir, "SKILL.md");
                if (!File.Exists(mdFile)) continue;
                var desc = (await File.ReadAllLinesAsync(mdFile, ct).ConfigureAwait(false))
                    .FirstOrDefault()?.TrimStart('#', ' ') ?? "";
                fsSection.AppendLine(CultureInfo.InvariantCulture, $"  /{name,-24} {desc}");
                found = true;
            }

            // Flat-format skills (name.md)
            foreach (var file in Directory.GetFiles(dir, "*.md"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (bundled.ContainsKey(name)) continue; // skip shadowed bundled skills
                if (!emitted.Add(name)) continue;        // already listed from a higher-priority dir
                var desc = (await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false))
                    .FirstOrDefault()?.TrimStart('#', ' ') ?? "";
                fsSection.AppendLine(CultureInfo.InvariantCulture, $"  /{name,-24} {desc}");
                found = true;
            }
        }

        if (found)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("Custom Skills:");
            sb.Append(fsSection);
        }

        if (sb.Length == 0) sb.AppendLine("No skills installed.");
        else
        {
            sb.AppendLine();
            sb.AppendLine("Run a skill with /<skillname> [args]. Preview with /skills show <name>.");
        }

        return sb.ToString().TrimEnd();
    }

    private static async Task<string> ShowSkillAsync(string name, CancellationToken ct)
    {
        var bundled = BundledSkills.Get(name);
        if (bundled is not null)
        {
            return $"# {bundled.Name}\n\n**Description:** {bundled.Description}\n\n---\n\n{bundled.Prompt}";
        }

        // Then filesystem
        foreach (var dir in GetSkillDirectories())
        {
            var skillDir = Path.Combine(dir, name);
            var mdFile = Path.Combine(skillDir, "SKILL.md");
            if (File.Exists(mdFile))
                return await File.ReadAllTextAsync(mdFile, ct).ConfigureAwait(false);

            var flatFile = Path.Combine(dir, $"{name}.md");
            if (File.Exists(flatFile))
                return await File.ReadAllTextAsync(flatFile, ct).ConfigureAwait(false);
        }

        return $"Skill '{name}' not found. Use /skills list to see available skills.";
    }

    private static IEnumerable<string> GetSkillDirectories()
    {
        var home = PathsHelper.UserHome;
        // User skills + project skills across all candidate dir names (.onecode/.agent/.claude).
        foreach (var userDir in ConfigDirPaths.EnumerateExisting(home, Constants.Subdirs.Skills))
            yield return userDir;
        foreach (var projectDir in ConfigDirPaths.EnumerateExisting(Directory.GetCurrentDirectory(), Constants.Subdirs.Skills))
            yield return projectDir;
    }
}
