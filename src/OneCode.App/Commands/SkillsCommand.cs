using System.Text;
using OneCode.Core.Skills;
using OneCode.App.Services.Skills;

namespace OneCode.App.Commands;

/// <summary>
/// /skills — list and inspect skills. Execution is via the dynamic slash command
/// <c>/&lt;skillname&gt;</c> registered by <see cref="SkillCommandSource"/>.
/// </summary>
public sealed class SkillsCommand(SkillCatalog catalog) : Command
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

    private async Task<string> ListSkillsAsync(CancellationToken ct)
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

        // Custom skills (from catalog, resolved with precedence and valid frontmatter)
        var allSkills = catalog.LoadUserInvocableSkills();
        var customSkills = allSkills
            .Where(s => !bundled.ContainsKey(s.Name))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (customSkills.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("Custom Skills:");
            foreach (var skill in customSkills)
            {
                var desc = string.IsNullOrWhiteSpace(skill.Description) ? "" : skill.Description;
                sb.AppendLine(CultureInfo.InvariantCulture, $"  /{skill.Name,-24} {desc}");
            }
        }

        if (sb.Length == 0) sb.AppendLine("No skills installed.");
        else
        {
            sb.AppendLine();
            sb.AppendLine("Run a skill with /<skillname> [args]. Preview with /skills show <name>.");
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<string> ShowSkillAsync(string name, CancellationToken ct)
    {
        var bundled = BundledSkills.Get(name);
        if (bundled is not null)
        {
            return $"# {bundled.Name}\n\n**Description:** {bundled.Description}\n\n---\n\n{bundled.Prompt}";
        }

        // Filesystem: 逆序枚举高优先级目录（项目级 > 用户级），与执行覆盖语义一致
        foreach (var dir in catalog.GetSkillDirectories().Reverse())
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
}
