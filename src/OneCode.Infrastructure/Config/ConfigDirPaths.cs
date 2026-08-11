namespace OneCode.Infrastructure.Config;

/// <summary>
/// Resolves configuration directory paths for read vs. write semantics.
/// <para>
/// Read/discover/watch actions must honour <see cref="Constants.App.ConfigDirCandidates"/>
/// (currently <c>.onecode</c>, <c>.agent</c>, <c>.claude</c>) so plugins/skills placed under
/// any of these directories are picked up. Write/install actions keep targeting the primary
/// candidate (<see cref="Constants.App.ConfigDirName"/>) so the on-disk location stays stable.
/// </para>
/// </summary>
public static class ConfigDirPaths
{
    /// <summary>
    /// Primary candidate directory for <em>writes</em>:
    /// <c>{parent}/{ConfigDirName}/{subdir}</c>.
    /// </summary>
    public static string GetPrimaryDir(string parent, string subdir) =>
        Path.Combine(parent, Constants.App.ConfigDirName, subdir);

    /// <summary>
    /// Enumerates <em>existing</em> candidate directories for <em>reads</em>, in priority order
    /// (<c>.onecode</c> → <c>.agent</c> → <c>.claude</c>). Results are case-insensitively
    /// de-duplicated by their fully-qualified real path.
    /// </summary>
    public static IEnumerable<string> EnumerateExisting(string parent, string subdir)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cfg in Constants.App.ConfigDirCandidates)
        {
            var path = Path.Combine(parent, cfg, subdir);
            if (!Directory.Exists(path)) continue;
            var real = Path.GetFullPath(path);
            if (seen.Add(real)) yield return real;
        }
    }

    /// <summary>
    /// Enumerates <em>all</em> candidate directory paths for <em>reads</em>, whether or not they
    /// exist on disk, in priority order. Useful for setting up <see cref="FileSystemWatcher"/>
    /// instances ahead of directory creation, or for callers that perform their own existence
    /// checks. Fully-qualified duplicates are removed.
    /// </summary>
    public static IEnumerable<string> EnumerateAll(string parent, string subdir)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cfg in Constants.App.ConfigDirCandidates)
        {
            var path = Path.Combine(parent, cfg, subdir);
            var real = Path.GetFullPath(path);
            if (seen.Add(real)) yield return real;
        }
    }
}
