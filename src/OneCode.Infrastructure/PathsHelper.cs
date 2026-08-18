using OneCode.Core.IO;
using OneCode.Core.Results;
using OneCode.Infrastructure.Config;
using CoreConstants = OneCode.Core.Constants;

namespace OneCode.Infrastructure;

/// <summary>
/// Helper for resolving and validating file paths.
/// </summary>
public static class PathsHelper
{
    private static readonly Lazy<string> s_userHome = new(ResolveUserHome);

    /// <summary>
    /// Maximum file size for read-into-memory operations (10 MB).
    /// Files larger than this are rejected to prevent OOM.
    /// Users should use Read's offset/limit parameters for large files.
    /// </summary>
    public const long MaxFileReadSize = 10 * 1024 * 1024;

    /// <summary>
    /// 用户主目录（~）。优先级：HOME → USERPROFILE → Environment.SpecialFolder.UserProfile → CurrentDirectory。
    /// 缓存为 Lazy 以避免重复 P/Invoke。
    /// </summary>
    public static string UserHome => s_userHome.Value;

    /// <summary>
    /// OneCode 用户级配置目录（~/.onecode）。
    /// </summary>
    public static string GetUserConfigDir() => Path.Combine(UserHome, Constants.App.ConfigDirName);

    private static string ResolveUserHome()
    {
        return Environment.GetEnvironmentVariable(CoreConstants.EnvVars.Home)
            ?? Environment.GetEnvironmentVariable(CoreConstants.EnvVars.UserHomeWindows)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            ?? Environment.CurrentDirectory;
    }

    /// <summary>
    /// Normalises a path for comparison: resolves to a canonical absolute path via
    /// <see cref="Path.GetFullPath(string)"/> and trims trailing directory separators.
    /// Falls back to a best-effort normalisation (forward-slash + trim) if the path
    /// contains invalid characters that <see cref="Path.GetFullPath(string)"/> rejects.
    /// </summary>
    /// <param name="path">Path to normalise. Null/empty returns as-is.</param>
    /// <returns>Canonical absolute path without trailing separators.</returns>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        var trimmed = path.Trim();
        try
        {
            return Path.GetFullPath(trimmed)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex)
        {
            // 纯静态方法无法注入 ILogger，按 §5.1 兜底使用 Debug.WriteLine。
            // 常见触发场景：路径含非法字符（Path.GetFullPath 抛 ArgumentException）。
            System.Diagnostics.Debug.WriteLine($"PathsHelper.NormalizePath fallback for '{path}': {ex.Message}");
            return trimmed.Replace('\\', '/').TrimEnd('/');
        }
    }

    /// <summary>
    /// Replaces characters that are invalid in file names with a safe replacement character.
    /// Use this to derive a safe file name from arbitrary input (git refs, repo names, etc.).
    /// </summary>
    /// <param name="key">Input string that may contain invalid file name characters.</param>
    /// <param name="replacement">Character to substitute for invalid chars. Defaults to '_'.</param>
    public static string SanitizeFileName(string key, char replacement = '_')
    {
        if (string.IsNullOrEmpty(key))
            return key;

        var invalid = Path.GetInvalidFileNameChars();
        if (key.AsSpan().IndexOfAny(invalid) < 0)
            return key;

        var sb = new StringBuilder(key.Length);
        foreach (var ch in key)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? replacement : ch);
        return sb.ToString();
    }

    /// <summary>
    /// Expands ~ to the user's home directory.
    /// </summary>
    public static string ExpandHome(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        if (path.StartsWith("~", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (path.Length == 1)
                return home;
            if ((path.Length > 1 && path[1] == '/') || (path.Length > 1 && path[1] == '\\'))
                return Path.Combine(home, path.Substring(2));
            return Path.Combine(home, path.Substring(1));
        }
        return path;
    }

    /// <summary>
    /// Resolves a path relative to a base directory.
    /// </summary>
    public static string Resolve(string path, string? baseDir = null)
    {
        var expanded = ExpandHome(path);
        if (Path.IsPathRooted(expanded))
            return Path.GetFullPath(expanded);
        if (!string.IsNullOrEmpty(baseDir))
            return Path.GetFullPath(Path.Combine(baseDir, expanded));
        return Path.GetFullPath(expanded);
    }

    /// <summary>
    /// Safely resolves a path and validates it's within the working directory.
    /// Rejects paths that fall inside platform-sensitive directories when the path
    /// is outside the working directory (explicit absolute-path access to protected
    /// system directories is blocked even with a valid traversal check).
    /// </summary>
    public static Result<string> SafeResolve(string path, string workingDir)
        => SafeResolve(path, workingDir, additionalDirs: null);

    /// <summary>
    /// Safely resolves a path and validates it's within the working directory or
    /// any of the <paramref name="additionalDirs"/> (e.g. directories added via
    /// <c>/add-dir</c>). Rejects paths that fall inside platform-sensitive
    /// directories when the path is outside all allowed roots.
    /// </summary>
    /// <param name="path">Absolute or relative path. Relative paths resolve against <paramref name="workingDir"/>.</param>
    /// <param name="workingDir">Primary working directory (root for relative-path resolution).</param>
    /// <param name="additionalDirs">Additional allowed roots. May be null or empty.</param>
    public static Result<string> SafeResolve(string path, string workingDir, IEnumerable<string>? additionalDirs)
    {
        var resolved = Resolve(path, workingDir);

        if (PathBoundary.IsWithinDirectory(resolved, workingDir))
            return Result<string>.Success(resolved);

        if (additionalDirs is not null)
        {
            foreach (var dir in additionalDirs)
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;
                try
                {
                    if (PathBoundary.IsWithinDirectory(resolved, dir))
                        return Result<string>.Success(resolved);
                }
                catch (ArgumentException) { /* skip invalid dir entries */ }
                catch (NotSupportedException) { /* skip invalid dir entries */ }
            }
        }

        if (ProtectedPaths.IsProtected(resolved))
            return Result<string>.Failure(
                $"Access denied: '{path}' is inside a protected system directory. " +
                "Move your project to a non-sensitive location.");

        return Result<string>.Failure(
            $"Path '{path}' is outside the working directory '{workingDir}'. " +
            "Use a path inside that directory (for example '.'), or /add-dir to grant access to additional roots.");
    }
}
