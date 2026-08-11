using Microsoft.Extensions.FileSystemGlobbing;

namespace OneCode.App.Tools;

/// <summary>
/// Predefined ignore rules for file system tools (Glob, LS, tree scans).
///
/// Rationale: showing the LLM fewer irrelevant files (build outputs, caches, VCS internals)
/// reduces noise, prevents unnecessary sequential file reads, and lowers the risk of 429s
/// caused by the model trying to inspect every visible file.
/// </summary>
public static class FileIgnore
{
    /// <summary>
    /// Directory names that should be skipped at any depth of the tree.
    /// Matched by exact segment name (e.g. "node_modules" anywhere in the path).
    /// </summary>
    public static readonly IReadOnlySet<string> Folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Package managers
        "node_modules",
        "bower_components",
        ".pnpm-store",
        "vendor",
        ".npm",

        // Build outputs
        "dist",
        "build",
        "out",
        ".next",
        "target",
        ".output",

        // .NET / Java build outputs (already in old list but kept explicit)
        "bin",
        "obj",
        ".gradle",

        // VCS
        ".git",
        ".svn",
        ".hg",

        // Editors / IDEs
        ".vs",
        ".vscode",
        ".idea",

        // Cloud / framework infra
        ".turbo",
        ".sst",
        "desktop",

        // Runtime caches
        ".cache",
        ".webkit-cache",
        "__pycache__",
        ".pytest_cache",
        "mypy_cache",
        ".history",
    };

    /// <summary>
    /// Glob patterns for individual files to ignore. Applied after folder filtering.
    /// </summary>
    public static readonly IReadOnlyList<string> FilePatterns =
    [
        // Editor swap / backup
        "**/*.swp",
        "**/*.swo",

        // Python bytecode
        "**/*.pyc",

        // OS metadata
        "**/.DS_Store",
        "**/Thumbs.db",

        // Logs & temp
        "**/logs/**",
        "**/tmp/**",
        "**/temp/**",
        "**/*.log",

        // Test coverage outputs
        "**/coverage/**",
        "**/.nyc_output/**",
    ];

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="relativePath"/> should be ignored.
    /// </summary>
    /// <param name="relativePath">
    /// Relative path (using either slash direction) from the search root.
    /// </param>
    /// <param name="extraPatterns">Additional glob patterns to apply (e.g. user config).</param>
    /// <param name="whitelist">Paths matching any whitelist pattern are never ignored.</param>
    public static bool IsIgnored(
        string relativePath,
        IEnumerable<string>? extraPatterns = null,
        IEnumerable<string>? whitelist = null)
    {
        if (string.IsNullOrEmpty(relativePath))
            return false;

        // Whitelist takes priority.
        if (whitelist is not null)
        {
            var wlMatcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            foreach (var p in whitelist)
                wlMatcher.AddInclude(p);
            if (wlMatcher.Match(relativePath).HasMatches)
                return false;
        }

        // Check each path segment against the folder block-list.
        var parts = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (Folders.Contains(part))
                return true;
        }

        // Check file glob patterns.
        var allPatterns = extraPatterns is null
            ? FilePatterns
            : [.. FilePatterns, .. extraPatterns];

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        foreach (var p in allPatterns)
            matcher.AddInclude(p);

        return matcher.Match(relativePath).HasMatches;
    }

    /// <summary>
    /// Adds all <see cref="Folders"/> and <see cref="FilePatterns"/> as exclude rules
    /// to an existing <see cref="Matcher"/> instance.
    /// </summary>
    public static void ApplyExcludes(Matcher matcher)
    {
        foreach (var dir in Folders)
        {
            matcher.AddExclude($"**/{dir}/**");
            matcher.AddExclude($"{dir}/**");
        }

        foreach (var pattern in FilePatterns)
            matcher.AddExclude(pattern);
    }
}
