using System.Text.RegularExpressions;
using OneCode.Core.Skills;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Skills;

namespace OneCode.App.Services.Skills;

/// <summary>Single source of truth for skill directories, slash discovery and prompt rendering.</summary>
public sealed partial class SkillCatalog(string workingDir)
{
    private readonly string _workingDir = Path.GetFullPath(workingDir);

    public IReadOnlyList<string> GetSkillDirectories()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path)
        {
            if (!Directory.Exists(path)) return;
            var fullPath = Path.GetFullPath(path);
            if (seen.Add(fullPath)) result.Add(fullPath);
        }

        Add(Path.Combine(AppContext.BaseDirectory, Constants.Subdirs.Skills));
        foreach (var path in ConfigDirPaths.EnumerateExisting(PathsHelper.UserHome, Constants.Subdirs.Skills))
            Add(path);
        foreach (var path in ConfigDirPaths.EnumerateExisting(_workingDir, Constants.Subdirs.Skills))
            Add(path);
        return result;
    }

    public IReadOnlyList<SkillDocument> LoadUserInvocableSkills()
    {
        var skills = new Dictionary<string, SkillDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundled in BundledSkills.All.Values)
        {
            skills[bundled.Name] = new SkillDocument(
                bundled.Name, bundled.Description, bundled.Prompt,
                ArgumentNames: InferPlaceholderNames(bundled.Prompt));
        }

        foreach (var dir in GetSkillDirectories())
        {
            foreach (var path in EnumerateSkillFiles(dir))
            {
                var fallbackName = Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileName(Path.GetDirectoryName(path))
                    : Path.GetFileNameWithoutExtension(path);
                if (!TryLoad(path, fallbackName!, out var skill) || !skill.UserInvocable)
                    continue;
                skills[skill.Name] = skill;
            }
        }
        return skills.Values.ToList();
    }

    public SkillDocument? Find(string name) => LoadUserInvocableSkills()
        .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string Render(SkillDocument skill, IReadOnlyList<string> args)
    {
        var joined = string.Join(" ", args);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < skill.ArgumentNames.Count; i++)
            values[skill.ArgumentNames[i]] = i < args.Count ? args[i] : string.Empty;

        var resolved = skill.Body.Replace("$ARGUMENTS", joined, StringComparison.Ordinal);
        return NamedPlaceholderRegex().Replace(resolved, match =>
        {
            var name = match.Groups["name"].Value;
            if (values.TryGetValue(name, out var value)) return value;
            if (skill.ArgumentNames.Count == 1 || skill.ArgumentNames.Count == 0)
                return joined;
            return match.Value;
        });
    }

    private static IEnumerable<string> EnumerateSkillFiles(string dir)
    {
        foreach (var skillDir in Directory.EnumerateDirectories(dir))
        {
            var file = Path.Combine(skillDir, "SKILL.md");
            if (File.Exists(file)) yield return file;
        }
        foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
            yield return file;
    }

    private static bool TryLoad(string path, string fallbackName, out SkillDocument skill)
    {
        try
        {
            return SkillFrontmatterParser.TryParse(File.ReadAllText(path), fallbackName, out skill);
        }
        catch (IOException)
        {
            skill = default!;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            skill = default!;
            return false;
        }
    }

    private static string[] InferPlaceholderNames(string prompt) => NamedPlaceholderRegex().Matches(prompt)
        .Select(m => m.Groups["name"].Value)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    [GeneratedRegex(@"\{(?<name>[A-Za-z][A-Za-z0-9_-]*)\}")]
    private static partial Regex NamedPlaceholderRegex();
}
