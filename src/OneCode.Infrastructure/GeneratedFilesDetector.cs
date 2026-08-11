namespace OneCode.Infrastructure;

/// <summary>
/// Detects generated/generated files that should not be tracked by git
/// or shown in diffs. Uses file extensions, naming patterns, and common
/// build output directory conventions.
/// </summary>
public static class GeneratedFilesDetector
{
    /// <summary>Common generated file extensions.</summary>
    private static readonly HashSet<string> GeneratedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Compiled
        ".dll", ".exe", ".pdb", ".ilk", ".exp", ".lib", ".a", ".so", ".dylib",
        // Intermediate
        ".obj", ".o", ".class", ".pyc", ".pyo", ".tlog", ".lastbuildstate",
        // Package/dependency
        ".nupkg", ".whl", ".jar",
        // Maps/debug
        ".map", ".sourcemap",
        // IDE
        ".suo", ".user", ".ncb", ".dbmdl", ".jfm",
    };

    /// <summary>Common generated directory names.</summary>
    private static readonly HashSet<string> GeneratedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "out", "build", "dist", "target", "node_modules",
        "__pycache__", ".pytest_cache", ".mypy_cache", ".tox",
        ".vs", ".idea", ".eclipse", ".settings",
        "packages", ".nuget",
        "coverage", ".nyc_output",
    };

    /// <summary>Check if a directory is a known generated/build output directory.</summary>
    public static bool IsGeneratedDirectory(string dirPath)
    {
        var parts = dirPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var part in parts)
        {
            if (GeneratedDirs.Contains(part))
                return true;
        }
        return false;
    }

    /// <summary>Check if an extension indicates a generated file.</summary>
    public static bool IsGeneratedExtension(string ext)
    {
        if (string.IsNullOrEmpty(ext)) return false;
        return GeneratedExtensions.Contains(ext)
            || GeneratedExtensions.Contains(ext.ToLowerInvariant());
    }
}
