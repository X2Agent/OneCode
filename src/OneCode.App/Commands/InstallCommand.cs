using OneCode.Infrastructure;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Commands;

/// <summary>
/// /install — install a skill from a local directory or a git repository.
///
/// Sources:
///   /install &lt;local-path&gt;     — copy a local skill directory into the skills folder
///   /install &lt;git-url&gt;        — clone a git repository (https://…, git://…, ssh)
///
/// Git URLs are auto-detected — no need for an explicit <c>git</c> subcommand.
///
/// Scope:
///   -g / --global                — install to the user-level directory (~/.onecode/skills/)
///   (default)                    — install to the project-level directory (&lt;cwd&gt;/.onecode/skills/)
///
/// The directory name (or repo name for git sources) becomes the skill name.
/// Existing skills at the destination are overwritten.
/// </summary>
public sealed class InstallCommand(IGitHelper gitHelper) : Command
{
    public override string Name => "install";
    public override string Description => "Install a skill from a local directory or git repository";
    public override CommandCategory Category => CommandCategory.Skill;
    public override string? ArgumentHint => "<path|git-url> [-g|--global]";

    public override async Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        // /install's core responsibility is installation.
        // Listing is delegated to /skills list to avoid maintaining two list implementations.
        if (args.Length == 0 || args[0] is "--list" or "list")
            return CommandResult.Text("Use '/skills list' to view installed skills.");

        // Parse flags: -g / --global switches scope to user-level.
        var global = false;
        var positional = new List<string>(args.Length);
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "-g":
                case "--global":
                    global = true;
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                        return CommandResult.Error($"Unknown option: {arg}. Supported: -g, --global");
                    positional.Add(arg);
                    break;
            }
        }

        if (positional.Count == 0)
            return CommandResult.Error("Usage: /install <path|git-url> [-g|--global]");

        var destRoot = ResolveSkillsRoot(global);
        Directory.CreateDirectory(destRoot);

        var source = positional[0];

        // Git URL → clone; otherwise treat as a local directory path.
        if (IsGitUrl(source))
            return await InstallFromGitAsync(source, destRoot, ct).ConfigureAwait(false);

        return InstallFromLocalDirectory(source, destRoot);
    }

    // Destination resolution

    /// <summary>
    /// Resolves the skills root directory.
    /// <para>
    /// Global: <c>~/.onecode/skills/</c> (user-level, shared across projects).
    /// Project: <c>&lt;cwd&gt;/.onecode/skills/</c> (project-local).
    /// </para>
    /// Writes always target the primary candidate (<c>.onecode</c>) per
    /// <see cref="ConfigDirPaths.GetPrimaryDir"/>.
    /// </summary>
    internal static string ResolveSkillsRoot(bool global)
    {
        var parent = global
            ? PathsHelper.UserHome
            : Directory.GetCurrentDirectory();
        return ConfigDirPaths.GetPrimaryDir(parent, Constants.Subdirs.Skills);
    }

    // Local directory installation

    private static CommandResult InstallFromLocalDirectory(string sourcePath, string destRoot)
    {
        if (!Directory.Exists(sourcePath))
            return CommandResult.Error($"Source path not found: {sourcePath}");

        var destDir = Path.Combine(destRoot, Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        try
        {
            CopyDirectory(sourcePath, destDir);
            return CommandResult.Text($"Installed skill to: {destDir}");
        }
        catch (Exception ex)
        {
            return CommandResult.Error($"Install failed: {ex.Message}");
        }
    }

    // Git installation

    /// <summary>
    /// Clones a git repository into a temp directory via <see cref="IGitHelper"/>,
    /// validates it contains a skill (SKILL.md or *.md), then copies the relevant
    /// content into the skills root.
    /// </summary>
    private async Task<CommandResult> InstallFromGitAsync(
        string gitUrl, string destRoot, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gitUrl))
            return CommandResult.Error("Git URL is empty.");

        var skillName = ExtractRepoName(gitUrl);
        if (string.IsNullOrEmpty(skillName))
            return CommandResult.Error($"Could not determine skill name from URL: {gitUrl}");

        var destDir = Path.Combine(destRoot, skillName);

        // Clone into a temp directory first so we can validate before committing.
        var tempDir = Path.Combine(Path.GetTempPath(), $"onecode-skill-{skillName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var cloneResult = await gitHelper.RunAsync(
                ["clone", "--depth", "1", "--quiet", gitUrl, tempDir], ct).ConfigureAwait(false);

            if (cloneResult is null || !cloneResult.Success)
            {
                var detail = cloneResult is null
                    ? "git is not available."
                    : cloneResult.Stderr.Trim();
                return CommandResult.Error(
                    $"git clone failed. {detail} URL: {gitUrl}");
            }

            // Validate: the repo root (or a skills/ subdirectory) must contain at least
            // one .md file to be a usable skill.
            var skillSourceDir = ResolveSkillContentDir(tempDir);
            if (skillSourceDir is null)
                return CommandResult.Error(
                    $"Repository cloned but no skill content found (expected SKILL.md or *.md " +
                    $"at repo root or under a 'skills/' subdirectory). URL: {gitUrl}");

            CopyDirectory(skillSourceDir, destDir);

            return CommandResult.Text($"Installed skill '{skillName}' from git to: {destDir}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CommandResult.Error($"Git install failed: {ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    /// <summary>
    /// Determines whether a string looks like a git URL that should be cloned
    /// rather than treated as a local path.
    /// </summary>
    internal static bool IsGitUrl(string source)
    {
        if (source.StartsWith("git://", StringComparison.OrdinalIgnoreCase))
            return true;
        if (source.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            // GitHub/GitLab/etc. URLs — treat as git if they look like a repo URL.
            // We intentionally keep this broad; if the clone fails the user gets a clear error.
            return true;
        }
        // SCP-style: git@github.com:user/repo.git
        if (source.Contains('@') && source.Contains(':'))
            return true;
        if (source.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>
    /// Extracts the repository name from a git URL to use as the skill name.
    /// <example>
    /// https://github.com/user/my-skill.git → my-skill
    /// git@github.com:user/my-skill.git     → my-skill
    /// </example>
    /// </summary>
    internal static string ExtractRepoName(string url)
    {
        // Strip query/fragment for URL-style inputs.
        var clean = url;
        var qIdx = clean.IndexOfAny(['?', '#']);
        if (qIdx >= 0) clean = clean[..qIdx];

        // Remove trailing slash or .git suffix.
        clean = clean.TrimEnd('/');
        if (clean.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            clean = clean[..^4];

        // Take the last path segment.
        var lastSlash = clean.LastIndexOfAny(['/', ':']);
        var name = lastSlash >= 0 ? clean[(lastSlash + 1)..] : clean;

        // Sanitize: only keep chars that are safe for a directory name.
        return PathsHelper.SanitizeFileName(name);
    }

    /// <summary>
    /// Resolves the directory containing skill content from a cloned repo.
    /// Checks repo root first, then a <c>skills/</c> subdirectory.
    /// Returns null if no <c>.md</c> file is found.
    /// </summary>
    private static string? ResolveSkillContentDir(string repoRoot)
    {
        if (HasSkillContent(repoRoot))
            return repoRoot;

        var skillsSub = Path.Combine(repoRoot, "skills");
        if (Directory.Exists(skillsSub) && HasSkillContent(skillsSub))
            return skillsSub;

        // Fallback: look for a single sub-directory that contains skill content.
        foreach (var sub in Directory.GetDirectories(repoRoot))
        {
            if (HasSkillContent(sub))
                return sub;
        }

        return null;
    }

    /// <summary>
    /// A directory is considered to contain skill content if it has a SKILL.md
    /// file or at least one .md file.
    /// </summary>
    private static bool HasSkillContent(string dir) =>
        File.Exists(Path.Combine(dir, "SKILL.md")) ||
        Directory.GetFiles(dir, "*.md").Length > 0;

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)));
    }
}
