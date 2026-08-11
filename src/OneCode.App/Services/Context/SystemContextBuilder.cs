using System.Text;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Abstractions;
using OneCode.Infrastructure.Config;
namespace OneCode.App.Services.Context;

/// <summary>
/// Builds system and user context for prompts.
/// </summary>
public sealed class ContextBuilder
{
    private const int MaxSystemContextChars = 2000; // Cap injected OS/git/platform context size
    private const int MaxRecentCommits = 5;

    private readonly GitInfo _gitInfo;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<ContextBuilder> _logger;

    public ContextBuilder(GitInfo gitInfo, IProcessRunner processRunner, ILogger<ContextBuilder> logger)
    {
        _gitInfo = gitInfo;
        _processRunner = processRunner;
        _logger = logger;
    }

    public async Task<string> BuildSystemContextAsync(
        string workingDirectory,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();

        sb.AppendLine(CultureInfo.InvariantCulture, $"Operating System: {GetOsDescription()}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Working Directory: {workingDirectory}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Current Time: {DateTimeOffset.Now:yyyy/MM/dd HH:mm:ss} ({TimeZoneInfo.Local.DisplayName}, UTC{(TimeZoneInfo.Local.BaseUtcOffset >= TimeSpan.Zero ? "+" : "")}{TimeZoneInfo.Local.BaseUtcOffset:hh\\:mm})");

        try
        {
            var status = await _gitInfo.GetStatusAsync(workingDirectory, ct).ConfigureAwait(false);
            if (status != null)
            {
                List<string> gitParts = [];
                if (!string.IsNullOrWhiteSpace(status.Branch))
                    gitParts.Add($"Branch: {status.Branch.Trim()}");
                if (status.IsDirty)
                    gitParts.Add($"Dirty: {status.UncommitedChanges} uncommitted changes, {status.UntrackedFiles} untracked files");
                if (!string.IsNullOrWhiteSpace(status.RemoteUrl))
                    gitParts.Add($"Remote: {status.RemoteUrl.Trim()}");

                if (gitParts.Count > 0)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"Git: {string.Join(", ", gitParts)}");
                }

                var recentCommits = await GetRecentCommitsAsync(workingDirectory, ct).ConfigureAwait(false);
                if (recentCommits.Count > 0)
                {
                    sb.AppendLine("Recent commits:");
                    foreach (var commit in recentCommits)
                    {
                        // Truncate each commit line to fit within overall budget
                        var truncated = commit.Length > 100 ? commit[..100] + "..." : commit;
                        sb.AppendLine(CultureInfo.InvariantCulture, $"  {truncated}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get git status for system context in {WorkingDir}", workingDirectory);
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"Platform: {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "Unknown")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Shell: {Environment.GetEnvironmentVariable("SHELL") ?? "cmd.exe"}");

        var result = sb.ToString();
        if (result.Length > MaxSystemContextChars)
            result = result[..MaxSystemContextChars];

        return result;
    }

    public async Task<string> BuildUserContextAsync(
        string workingDirectory,
        IReadOnlyList<string>? additionalDirectories = null,
        CancellationToken ct = default)
    {
        var memoryFiles = await DiscoverMemoryFilesAsync(workingDirectory, additionalDirectories, ct).ConfigureAwait(false);

        if (memoryFiles.Count == 0)
            return "(No additional user context)";

        var sb = new StringBuilder();
        sb.AppendLine("Codebase and user instructions are shown below. Be sure to adhere to these instructions. IMPORTANT: These instructions OVERRIDE any default behavior and you MUST follow them exactly as written.");
        sb.AppendLine();

        foreach (var (path, type, content) in memoryFiles)
        {
            var description = type switch
            {
                MemoryFileType.Project => " (project instructions, checked into the codebase)",
                MemoryFileType.Local => " (user's private project instructions, not checked in)",
                _ => ""
            };

            sb.AppendLine(CultureInfo.InvariantCulture, $"Contents of {path}{description}:");
            sb.AppendLine();
            sb.AppendLine(content.Trim());
            sb.AppendLine();
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"Local Date: {DateTimeOffset.Now:yyyy-MM-dd}");

        return sb.ToString();
    }

    /// <summary>
    /// Discover project context files (AGENTS.md, .onecode/AGENTS.md, .onecode/rules/*.md, AGENTS.local.md)
    /// walking from filesystem root down to CWD, matching TS getMemoryFiles() behavior.
    /// </summary>
    private async Task<List<(string Path, MemoryFileType Type, string Content)>> DiscoverMemoryFilesAsync(
        string workingDir,
        IReadOnlyList<string>? additionalDirectories,
        CancellationToken ct)
    {
        List<(string Path, MemoryFileType Type, string Content)> results = [];
        var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        List<string> dirs = [];
        var currentDir = workingDir;
        var root = Path.GetPathRoot(workingDir) ?? string.Empty;

        while (!string.IsNullOrEmpty(currentDir) && currentDir.Length >= root.Length)
        {
            dirs.Add(currentDir);
            if (string.Equals(currentDir, root, StringComparison.OrdinalIgnoreCase))
                break;
            var parent = Path.GetDirectoryName(currentDir);
            if (parent == null || string.Equals(parent, currentDir, StringComparison.OrdinalIgnoreCase))
                break;
            currentDir = parent;
        }

        dirs.Reverse(); // root → CWD

        foreach (var dir in dirs)
        {
            await ProcessDirectoryAsync(dir, MemoryFileType.Project, results, processedPaths, ct).ConfigureAwait(false);
        }

        // Additional directories (from --add-dir)
        if (additionalDirectories != null)
        {
            foreach (var addDir in additionalDirectories)
            {
                if (Directory.Exists(addDir))
                    await ProcessDirectoryAsync(addDir, MemoryFileType.Project, results, processedPaths, ct).ConfigureAwait(false);
            }
        }

        return results;
    }

    private async Task ProcessDirectoryAsync(
        string dir,
        MemoryFileType type,
        List<(string Path, MemoryFileType Type, string Content)> results,
        HashSet<string> processedPaths,
        CancellationToken ct)
    {
        var mdFiles = new[] { "AGENTS.md", "agents.md", $"{Constants.App.ConfigDirName}/AGENTS.md", $"{Constants.App.ConfigDirName}/agents.md" };
        foreach (var mdFile in mdFiles)
        {
            var fullPath = Path.Combine(dir, mdFile);
            if (File.Exists(fullPath) && processedPaths.Add(fullPath))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(content))
                        results.Add((fullPath, type, content));
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogDebug(ex, "Access denied reading memory file {Path}", fullPath);
                }
            }
        }

        // AGENTS.local.md
        var localPath = Path.Combine(dir, "AGENTS.local.md");
        if (File.Exists(localPath) && processedPaths.Add(localPath))
        {
            try
            {
                var content = await File.ReadAllTextAsync(localPath, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(content))
                    results.Add((localPath, MemoryFileType.Local, content));
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogDebug(ex, "Access denied reading memory file {Path}", localPath);
            }
        }

        // .onecode/rules/*.md
        var rulesDir = Path.Combine(dir, Constants.App.ConfigDirName, "rules");
        if (Directory.Exists(rulesDir))
        {
            try
            {
                var ruleFiles = Directory.GetFiles(rulesDir, "*.md", SearchOption.AllDirectories);
                foreach (var rulePath in ruleFiles)
                {
                    if (!processedPaths.Add(rulePath))
                        continue;

                    try
                    {
                        var content = await File.ReadAllTextAsync(rulePath, ct).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(content))
                            results.Add((rulePath, type, content));
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        _logger.LogDebug(ex, "Access denied reading rule file {Path}", rulePath);
                    }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogDebug(ex, "Access denied scanning rules directory {Path}", rulesDir);
            }
        }
    }

    /// <summary>
    /// Get recent git commits.
    /// </summary>
    private async Task<List<string>> GetRecentCommitsAsync(string workingDir, CancellationToken ct)
    {
        List<string> commits = [];
        try
        {
            var args = new[]
            {
                "log",
                $"--max-count={MaxRecentCommits}",
                "--pretty=format:%h %s (%cr)",
            };

            var result = await _processRunner.ExecuteWithArgumentListAsync(
                "git",
                args,
                workingDirectory: workingDir,
                ct: ct).ConfigureAwait(false);

            if (result != null && result.Success)
            {
                commits = result.Stdout
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Take(MaxRecentCommits)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get recent commits in {WorkingDir}", workingDir);
        }

        return commits;
    }

    private static string GetOsDescription()
    {
        if (OperatingSystem.IsWindows())
            return $"Windows {Environment.OSVersion.VersionString}";
        if (OperatingSystem.IsMacOS())
            return $"macOS {Environment.OSVersion.VersionString}";
        if (OperatingSystem.IsLinux())
            return $"Linux {Environment.OSVersion.VersionString}";
        return Environment.OSVersion.VersionString;
    }
}

internal enum MemoryFileType
{
    Project,
    Local
}
