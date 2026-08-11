using OneCode.Infrastructure;

namespace OneCode.Automation.Cron;

/// <summary>
/// Centralised path resolution and file I/O helpers for the cron subsystem.
/// Uses <see cref="PathsHelper"/> (Infrastructure) for user-config directory resolution.
/// </summary>
public static class CronPaths
{
    /// <summary>
    /// Directory under <c>~/{ConfigDir}/cron</c> where durable job JSON files live.
    /// </summary>
    public static string GetCronDirectory()
        => Path.Combine(PathsHelper.GetUserConfigDir(), "cron");

    /// <summary>
    /// Full path for a single job file: <c>~/{ConfigDir}/cron/{id}.json</c>.
    /// </summary>
    public static string GetJobFilePath(string id)
        => Path.Combine(GetCronDirectory(), $"{id}.json");

    /// <summary>
    /// Whether durable cron persistence is enabled at all. Disabled by default to preserve
    /// the previous "in-memory unless explicitly opted in" behaviour. Enable by setting
    /// <c>ONECODE_DURABLE_CRON=true</c>.
    /// </summary>
    public static bool IsDurableCronEnabled()
        => string.Equals(
            Environment.GetEnvironmentVariable("ONECODE_DURABLE_CRON"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Validate that a job ID contains only safe characters (alphanumeric and hyphen).
    /// Prevents path traversal via crafted IDs in deserialized JSON files.
    /// </summary>
    public static bool IsValidJobId(string id)
        => id.Length > 0 && id.All(c => char.IsLetterOrDigit(c) || c == '-');

    /// <summary>
    /// Atomically write content to a file via temp-file + <c>File.Move(overwrite: true)</c>.
    /// Prevents half-written files if the process crashes mid-write.
    /// </summary>
    public static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null) Directory.CreateDirectory(dir);
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, content, ct).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }
}
