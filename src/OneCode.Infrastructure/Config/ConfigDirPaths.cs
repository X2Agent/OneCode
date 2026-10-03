namespace OneCode.Infrastructure.Config;

/// <summary>
/// Resolves configuration directory paths.
/// <para>
/// Skill discovery honours the Agent Skills compatible layouts
/// (<c>.agents</c>/<c>.cursor</c> plus the OneCode directories) so skills placed under
/// any of these directories are picked up. Write/install actions keep targeting the primary
/// directory (<see cref="Constants.App.ConfigDirName"/>) so the on-disk location stays stable.
/// </para>
/// </summary>
public static class ConfigDirPaths
{
    /// <summary>
    /// Skill 发现候选目录（低 → 高覆盖顺序）：优先兼容 Agent Skills 生态目录
    /// （.agents/.cursor），再枚举 OneCode 本地目录。
    /// </summary>
    private static readonly IReadOnlyList<string> SkillConfigDirCandidates =
        [".agents", ".cursor", ".onecode", ".claude"];

    /// <summary>
    /// Primary candidate directory for <em>writes</em>:
    /// <c>{parent}/{ConfigDirName}/{subdir}</c>.
    /// </summary>
    public static string GetPrimaryDir(string parent, string subdir) =>
        Path.Combine(parent, Constants.App.ConfigDirName, subdir);

    /// <summary>
    /// Enumerates skill directories across the OneCode and Agent Skills compatible layouts.
    /// The order is the low-to-high override order, so later directories take precedence.
    /// </summary>
    public static IEnumerable<string> EnumerateSkillDirectories(string parent)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cfg in SkillConfigDirCandidates)
        {
            var path = Path.Combine(parent, cfg, Constants.Subdirs.Skills);
            if (!Directory.Exists(path)) continue;
            var real = Path.GetFullPath(path);
            if (seen.Add(real)) yield return real;
        }
    }
}
