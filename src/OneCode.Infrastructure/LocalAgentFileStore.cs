using Microsoft.Agents.AI;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using OneCode.Core.IO;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Abstractions;

namespace OneCode.Infrastructure;

/// <summary>
/// Local filesystem implementation of MAF <see cref="AgentFileStore"/> and <see cref="IFileSystem"/>.
///
/// <para>
/// Bridges MAF's file-store abstraction (relative-path-based, multi-backend) with the local
/// filesystem. Paths are resolved against the working directory from <see cref="IWorkingDirectoryAccessor"/>
/// and validated against the working directory and any additional directories.
/// </para>
/// </summary>
public sealed class LocalAgentFileStore : AgentFileStore, IFileSystem
{
    private readonly string _workingDirectory;
    private readonly IReadOnlyList<string>? _additionalDirectories;
    private readonly ILogger<LocalAgentFileStore>? _logger;

    public LocalAgentFileStore(
        IWorkingDirectoryAccessor wd,
        ILogger<LocalAgentFileStore>? logger = null)
    {
        _workingDirectory = wd.WorkingDirectory;
        _additionalDirectories = wd.AdditionalDirectories;
        _logger = logger;
    }

    // AgentFileStore abstract methods

    public override async Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = ResolvePath(path);
        try
        {
            return await File.ReadAllTextAsync(resolved, cancellationToken).ConfigureAwait(false);
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

    public override async Task WriteAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var resolved = ResolvePath(path);
        var dir = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(resolved, content, cancellationToken).ConfigureAwait(false);
    }

    public override Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = ResolvePath(path);
        if (File.Exists(resolved))
        {
            File.Delete(resolved);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = ResolvePath(path);
        return Task.FromResult(File.Exists(resolved));
    }

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var resolved = ResolvePath(path);
        if (!Directory.Exists(resolved))
            Directory.CreateDirectory(resolved);
        return Task.CompletedTask;
    }

    public override Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        var resolved = ResolvePath(directory);
        if (!Directory.Exists(resolved))
            return Task.FromResult<IReadOnlyList<FileStoreEntry>>(Array.Empty<FileStoreEntry>());

        try
        {
            var entries = new List<FileStoreEntry>();

            // Subdirectories first (MAF convention)
            foreach (var dir in Directory.GetDirectories(resolved))
            {
                entries.Add(new FileStoreEntry(Path.GetFileName(dir), FileStoreEntry.Directory));
            }

            foreach (var file in Directory.GetFiles(resolved))
            {
                entries.Add(new FileStoreEntry(Path.GetFileName(file), FileStoreEntry.File));
            }

            return Task.FromResult<IReadOnlyList<FileStoreEntry>>(entries);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult<IReadOnlyList<FileStoreEntry>>(Array.Empty<FileStoreEntry>());
        }
    }

    public override async Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string directory,
        string regexPattern,
        string? globPattern = null,
        bool recursive = false,
        CancellationToken cancellationToken = default)
    {
        var resolved = ResolvePath(directory);
        if (!Directory.Exists(resolved))
            return Array.Empty<FileSearchResult>();

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        // Determine which files to search
        IEnumerable<string> files;
        if (!string.IsNullOrWhiteSpace(globPattern))
        {
            var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            matcher.AddInclude(globPattern.Replace('\\', '/'));
            var dirInfo = new DirectoryInfoWrapper(new DirectoryInfo(resolved));
            var result = matcher.Execute(dirInfo);
            files = result.Files.Select(f => Path.GetFullPath(Path.Combine(resolved, f.Path)));
        }
        else
        {
            try
            {
                files = Directory.GetFiles(resolved, "*", searchOption);
            }
            catch (UnauthorizedAccessException)
            {
                return Array.Empty<FileSearchResult>();
            }
        }

        var regex = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var results = new List<FileSearchResult>();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            var matchingLines = new List<FileSearchMatch>();
            for (var i = 0; i < lines.Length; i++)
            {
                if (regex.IsMatch(lines[i]))
                {
                    matchingLines.Add(new FileSearchMatch
                    {
                        LineNumber = i + 1,
                        Line = lines[i],
                    });
                }
            }

            if (matchingLines.Count > 0)
            {
                var snippet = matchingLines.First().Line;
                if (snippet.Length > 200)
                    snippet = snippet[..200] + "...";

                results.Add(new FileSearchResult
                {
                    FileName = Path.GetFileName(file),
                    Snippet = snippet,
                    MatchingLines = matchingLines,
                });
            }
        }

        return results;
    }

    // IFileSystem methods

    async Task<string?> IFileSystem.ReadTextFileAsync(string path, CancellationToken ct)
    {
        return await ReadAsync(path, ct).ConfigureAwait(false);
    }

    async Task IFileSystem.WriteTextFileAsync(string path, string content, CancellationToken ct)
    {
        await WriteAsync(path, content, ct).ConfigureAwait(false);
    }

    IReadOnlyList<string> IFileSystem.FindFiles(
        string directory,
        string? patterns,
        string[]? excludeDirs)
    {
        var resolved = ResolvePath(directory);
        if (!Directory.Exists(resolved))
            return Array.Empty<string>();

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

        var exclude = excludeDirs ?? Array.Empty<string>();
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
            return Array.Empty<string>();
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

        string resolved;
        if (Path.IsPathRooted(expanded))
        {
            resolved = Path.GetFullPath(expanded);
        }
        else
        {
            resolved = Path.GetFullPath(Path.Combine(_workingDirectory, expanded));
        }

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
