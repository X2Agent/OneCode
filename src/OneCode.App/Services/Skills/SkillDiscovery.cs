using OneCode.Infrastructure.Skills;

namespace OneCode.App.Services.Skills;

/// <summary>
/// The single discovery rule for skills, shared by the model entry point and the slash-command entry point.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem this solves.</b> The two entry points used to disagree: the model saw whatever MAF's
/// file source found (recursive <c>SKILL.md</c> within its search depth, name must match the directory
/// name, MAF's YAML parser), while the slash commands saw a separate scan (top-level <c>*.md</c> plus
/// direct-child <c>SKILL.md</c>, the product parser, last-write-wins). A repository could therefore
/// expose a skill to one entry point and not the other, or expose two different bodies under one name.
/// </para>
/// <para>
/// <b>One rule.</b> <see cref="IsSharedSkillFile"/> and <see cref="FindSharedSkillFile"/> encode MAF's
/// rule for what the model can see, and both entry points go through them. The model side still lets
/// MAF do the reading (its parser is authoritative); this type decides <i>which</i> files qualify.
/// </para>
/// <para>
/// <b>Product-only set.</b> A directory may also contain a top-level <c>*.md</c> file, which the agent
/// never sees. Those files stay an explicitly <b>user-invocable-only</b> layout: they are a supported
/// authoring form, and promoting them to the model would silently widen its skill set.
/// </para>
/// <para>
/// <b>Precedence.</b> Later directories override earlier ones; within one directory the shared
/// <c>SKILL.md</c> wins over a legacy top-level file of the same name. Ordering is resolved here so
/// "which copy wins" has exactly one answer.
/// </para>
/// </remarks>
public static class SkillDiscovery
{
    /// <summary>Name of the file that defines a shared (model-visible) skill.</summary>
    public const string SharedSkillFileName = "SKILL.md";

    /// <summary>Whether a file is a shared skill definition the model can also see.</summary>
    public static bool IsSharedSkillFile(string path) =>
        Path.GetFileName(path).Equals(SharedSkillFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the shared skill file inside <paramref name="skillDirectory"/>, or null when the directory
    /// does not define one.
    /// </summary>
    /// <param name="skillDirectory">Directory that would be the skill root.</param>
    public static string? FindSharedSkillFile(string skillDirectory)
    {
        var candidate = Path.Combine(skillDirectory, SharedSkillFileName);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Enumerates the directories that define a shared skill, breadth-first by depth so shallower skills
    /// are discovered first, then alphabetically for a stable order.
    /// </summary>
    /// <param name="rootDirectory">Skill root to search.</param>
    /// <param name="maxDepth">
    /// Maximum depth below <paramref name="rootDirectory"/>. MAF's default search depth is 2.
    /// </param>
    public static IEnumerable<string> EnumerateSharedSkillDirectories(string rootDirectory, int maxDepth)
    {
        if (!Directory.Exists(rootDirectory) || maxDepth < 0)
            yield break;

        var level = new List<string> { Path.GetFullPath(rootDirectory) };

        for (var depth = 0; depth <= maxDepth && level.Count > 0; depth++)
        {
            var next = new List<string>();

            foreach (var directory in level.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (FindSharedSkillFile(directory) is not null)
                {
                    yield return directory;

                    // A directory holding SKILL.md is the skill root; MAF does not descend past it.
                    continue;
                }

                if (depth == maxDepth)
                    continue;

                IEnumerable<string> children;
                try
                {
                    children = Directory.EnumerateDirectories(directory);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                next.AddRange(children);
            }

            level = next;
        }
    }

    /// <summary>
    /// Loads the product-only top-level <c>*.md</c> layout from one directory.
    /// </summary>
    /// <remarks>
    /// These entries are user-invocable only: MAF does not read them, so the model never sees them.
    /// </remarks>
    public static IEnumerable<SkillDocument> LoadUserOnlySkills(string directory)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.md")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var path in files)
        {
            // SKILL.md belongs to the shared set, not to this layout.
            if (IsSharedSkillFile(path))
                continue;

            if (TryLoad(path, out var document) && document.UserInvocable)
                yield return document;
        }
    }

    /// <summary>Parses a skill file with the product parser.</summary>
    public static bool TryLoad(string path, out SkillDocument document)
    {
        try
        {
            return SkillFrontmatterParser.TryParse(File.ReadAllText(path), FallbackName(path), out document);
        }
        catch (IOException)
        {
            document = default!;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            document = default!;
            return false;
        }
    }

    /// <summary>
    /// Name used when a file's frontmatter omits one: the skill directory for <c>SKILL.md</c>, otherwise
    /// the file name without extension.
    /// </summary>
    /// <remarks>
    /// The directory-name rule mirrors MAF's requirement that a shared skill's frontmatter name match its
    /// parent directory, so a shared skill keeps one identity in both entry points.
    /// </remarks>
    public static string FallbackName(string path) =>
        IsSharedSkillFile(path)
            ? Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty
            : Path.GetFileNameWithoutExtension(path);
}
