using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using OneCode.Core.IO;
using OneCode.Core.Tools;

namespace OneCode.Infrastructure;

/// <summary>
/// Local filesystem implementation of <see cref="IFileSystem"/>.
///
/// <para>
/// Paths are resolved against the working directory from <see cref="IWorkingDirectoryAccessor"/>
/// and validated against the working directory and any additional directories.
/// </para>
/// <para>
/// This type deliberately does <b>not</b> implement MAF's <c>AgentFileStore</c>: no MAF component
/// consumes that abstraction on the product's own file tools. Harness working memory uses
/// <c>FileSystemAgentFileStore</c> with a project-scoped root (see <c>FileMemoryStorePaths</c>),
/// which is a different store instance and a different concern.
/// </para>
/// </summary>
public sealed class LocalAgentFileStore(
    IWorkingDirectoryAccessor wd,
    ILogger<LocalAgentFileStore>? logger = null) : IFileSystem
{
    private readonly string _workingDirectory = wd.WorkingDirectory;
    private readonly IReadOnlyList<string>? _additionalDirectories = wd.AdditionalDirectories;
    private readonly ILogger<LocalAgentFileStore>? _logger = logger;

    // IFileSystem methods

    async Task<string?> IFileSystem.ReadTextFileAsync(string path, CancellationToken ct)
    {
        var resolved = ResolvePath(path);
        try
        {
            return await File.ReadAllTextAsync(resolved, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    async Task IFileSystem.WriteTextFileAsync(string path, string content, CancellationToken ct)
    {
        var resolved = ResolvePath(path);
        var dir = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(resolved, content, ct).ConfigureAwait(false);
    }

    IReadOnlyList<string> IFileSystem.FindFiles(
        string directory,
        string? patterns,
        string[]? excludeDirs)
    {
        var resolved = ResolvePath(directory);
        if (!Directory.Exists(resolved))
            return [];

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);

        var includes = string.IsNullOrWhiteSpace(patterns)
            ? new[] { "**/*" }
            : patterns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var include in includes)
        {
            var normalised = include.Replace('\\', '/');
            var addPattern = normalised.Contains('/') ? normalised : $"**/{normalised}";
            matcher.AddInclude(addPattern);
        }

        var exclude = excludeDirs ?? [];
        foreach (var dir in exclude)
        {
            matcher.AddExclude($"**/{dir}/**");
            matcher.AddExclude($"{dir}/**");
        }

        try
        {
            var dirInfo = new DirectoryInfoWrapper(new DirectoryInfo(resolved));
            var result = matcher.Execute(dirInfo);
            return result.Files
                .Select(f => Path.GetFullPath(Path.Combine(resolved, f.Path)))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    bool IFileSystem.MatchesGlob(string filePath, string pattern)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        var normalised = pattern.Replace('\\', '/');
        var addPattern = normalised.Contains('/') ? normalised : $"**/{normalised}";
        matcher.AddInclude(addPattern);
        return matcher.Match(filePath.Replace('\\', '/')).HasMatches;
    }

    long IFileSystem.GetMtimeMs(string path)
    {
        var resolved = ResolvePath(path);
        if (File.Exists(resolved))
            return new DateTimeOffset(File.GetLastWriteTimeUtc(resolved)).ToUnixTimeMilliseconds();
        return 0;
    }

    // Path resolution

    /// <summary>
    /// Resolves a path (relative or absolute) against the working directory and validates
    /// it is within an allowed directory.
    /// </summary>
    private string ResolvePath(string path)
    {
        var expanded = PathsHelper.ExpandHome(path);

        var resolved = Path.IsPathRooted(expanded)
            ? Path.GetFullPath(expanded)
            : Path.GetFullPath(Path.Combine(_workingDirectory, expanded));

        if (PathBoundary.IsWithinDirectory(resolved, _workingDirectory))
            return resolved;

        if (_additionalDirectories is not null)
        {
            foreach (var dir in _additionalDirectories)
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;
                try
                {
                    if (PathBoundary.IsWithinDirectory(resolved, dir))
                        return resolved;
                }
                catch (ArgumentException) { /* skip invalid dir entries */ }
                catch (NotSupportedException) { /* skip invalid dir entries */ }
            }
        }

        _logger?.LogWarning("Path '{Path}' is outside the working directory and additional directories", path);
        throw new UnauthorizedAccessException(
            $"Path '{path}' is outside the working directory. " +
            "Use /add-dir to grant access to additional directories.");
    }
}
