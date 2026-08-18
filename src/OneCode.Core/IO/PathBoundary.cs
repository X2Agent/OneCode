namespace OneCode.Core.IO;

/// <summary>
/// Directory-containment checks shared by permission gating and file tools.
/// </summary>
public static class PathBoundary
{
    /// <summary>
    /// Returns whether <paramref name="path"/> is <paramref name="baseDir"/> itself
    /// or a descendant of it. Rejects prefix spoofs such as <c>C:\App</c> vs <c>C:\Application</c>.
    /// </summary>
    /// <remarks>
    /// Relative <paramref name="path"/> values resolve against the process current directory,
    /// not <paramref name="baseDir"/>. Callers that accept working-dir-relative input must
    /// resolve first (for example <c>Path.GetFullPath(path, workingDir)</c>).
    /// </remarks>
    public static bool IsWithinDirectory(
        string path,
        string baseDir,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var baseFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDir));

        if (full.Equals(baseFull, comparison))
            return true;

        // Root paths such as C:\ or / already end with a separator after trim;
        // do not append another one or children would be checked against C:\\ / //.
        var prefix = Path.EndsInDirectorySeparator(baseFull)
            ? baseFull
            : baseFull + Path.DirectorySeparatorChar;

        return full.StartsWith(prefix, comparison);
    }
}
