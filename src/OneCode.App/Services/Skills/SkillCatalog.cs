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
        List<string> result = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path)
        {
            if (!Directory.Exists(path)) return;
            var fullPath = Path.GetFullPath(path);
            if (seen.Add(fullPath)) result.Add(fullPath);
        }

        Add(Path.Combine(AppContext.BaseDirectory, Constants.Subdirs.Skills));
        foreach (var path in ConfigDirPaths.EnumerateSkillDirectories(PathsHelper.UserHome))
            Add(path);
        foreach (var path in ConfigDirPaths.EnumerateSkillDirectories(_workingDir))
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
            // Shared skills (the ones the model also sees) are read first so they win over a legacy
            // top-level file of the same name in the same directory.
            foreach (var skillDir in SkillDiscovery.EnumerateSharedSkillDirectories(
                dir, MaxSharedSkillSearchDepth))
            {
                var path = SkillDiscovery.FindSharedSkillFile(skillDir);
                if (path is null || !SkillDiscovery.TryLoad(path, out var shared) || !shared.UserInvocable)
                    continue;

                skills[shared.Name] = shared;
            }

            foreach (var legacy in SkillDiscovery.LoadUserOnlySkills(dir))
                skills.TryAdd(legacy.Name, legacy);
        }

        return skills.Values.ToList();
    }

    /// <summary>
    /// Search depth for shared skill directories. Matches MAF's default so both entry points look in the
    /// same places.
    /// </summary>
    private const int MaxSharedSkillSearchDepth = 2;

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

    private static string[] InferPlaceholderNames(string prompt) => NamedPlaceholderRegex().Matches(prompt)
        .Select(m => m.Groups["name"].Value)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    [GeneratedRegex(@"\{(?<name>[A-Za-z][A-Za-z0-9_-]*)\}")]
    private static partial Regex NamedPlaceholderRegex();
}
