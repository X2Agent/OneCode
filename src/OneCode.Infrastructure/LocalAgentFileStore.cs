using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using OneCode.Core.IO;
using OneCode.Core.Tools;

namespace OneCode.Infrastructure;

/// <summary>
/// 工作区文件系统访问器：glob 文件枚举 + 工作目录边界校验。
///
/// <para>
/// 路径基于 <see cref="IWorkingDirectoryAccessor"/> 的工作目录解析，
/// 并校验落在工作目录与附加目录（/add-dir）之内，越界抛 <see cref="UnauthorizedAccessException"/>。
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
    ILogger<LocalAgentFileStore>? logger = null)
{
    private readonly string _workingDirectory = wd.WorkingDirectory;
    private readonly IReadOnlyList<string>? _additionalDirectories = wd.AdditionalDirectories;
    private readonly ILogger<LocalAgentFileStore>? _logger = logger;

    /// <summary>
    /// Enumerates files under <paramref name="directory"/> matching comma-separated glob
    /// <paramref name="patterns"/>, excluding the given directory names.
    /// </summary>
    public IReadOnlyList<string> FindFiles(
        string directory,
        string? patterns = null,
        string[]? excludeDirs = null)
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
