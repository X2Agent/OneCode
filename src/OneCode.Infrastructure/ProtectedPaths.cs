namespace OneCode.Infrastructure;

/// <summary>
/// Platform-specific list of sensitive user directories that file tools should never
/// access without an explicit, user-visible confirmation.
///
/// These paths contain personal data, OS credentials, or application secrets.
/// Accessing them silently would be a privacy violation even when the binary has
/// the OS-level permission to do so.
/// </summary>
public static class ProtectedPaths
{
    /// <summary>
    /// Returns the set of absolute directory paths that are considered protected on the
    /// current platform. File tool implementations should call <see cref="IsProtected"/>
    /// before reading or listing any path.
    /// </summary>
    public static IReadOnlyList<string> GetPlatformPaths()
    {
        if (OperatingSystem.IsWindows())
            return WindowsPaths();

        if (OperatingSystem.IsMacOS())
            return MacOsPaths();

        // Linux: sensitive credential/personal directories under $HOME.
        // OS permissions alone are insufficient when the process runs as the owning user.
        return LinuxPaths();
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="absolutePath"/> is inside
    /// (or is equal to) one of the protected platform directories.
    /// </summary>
    public static bool IsProtected(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        foreach (var dir in GetPlatformPaths())
        {
            var dirWithSep = dir.EndsWith(Path.DirectorySeparatorChar)
                ? dir
                : dir + Path.DirectorySeparatorChar;

            if (full.StartsWith(dirWithSep, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(full, dir, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // Windows

    private static IReadOnlyList<string> WindowsPaths()
    {
        var profile = PathsHelper.UserHome;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        List<string> paths = [];

        // App data (credentials, tokens, browser profiles)
        AddIfNonEmpty(paths, appData);
        AddIfNonEmpty(paths, localAppData);

        // Personal folders
        foreach (var folder in new[]
        {
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.MyVideos,
        })
        {
            AddIfNonEmpty(paths, Environment.GetFolderPath(folder));
        }

        // Common locations not covered by SpecialFolder
        if (!string.IsNullOrEmpty(profile))
        {
            AddIfNonEmpty(paths, Path.Combine(profile, "Downloads"));
            AddIfNonEmpty(paths, Path.Combine(profile, "Desktop"));
            AddIfNonEmpty(paths, Path.Combine(profile, "OneDrive"));
        }

        return paths;
    }

    // macOS

    private static IReadOnlyList<string> MacOsPaths()
    {
        var home = PathsHelper.UserHome;
        if (string.IsNullOrEmpty(home)) return [];

        List<string> paths = [];

        // TCC-protected personal folders
        foreach (var sub in new[]
        {
            "Music", "Pictures", "Movies", "Downloads", "Desktop", "Documents", "Public",
        })
        {
            AddIfNonEmpty(paths, Path.Combine(home, sub));
        }

        // Library sub-directories containing sensitive app data
        var library = Path.Combine(home, "Library");
        foreach (var sub in new[]
        {
            "AddressBook", "Calendars", "Mail", "Messages", "Safari",
            "Cookies", "com.apple.TCC", "PersonalizationPortrait", "CoreSpotlight",
        })
        {
            AddIfNonEmpty(paths, Path.Combine(library, sub));
        }

        // System-level protected paths
        foreach (var p in new[]
        {
            "/.DocumentRevisions-V100",
            "/.Spotlight-V100",
            "/.Trashes",
            "/.fseventsd",
        })
        {
            paths.Add(p);
        }

        return paths;
    }

    // Linux

    private static IReadOnlyList<string> LinuxPaths()
    {
        var home = PathsHelper.UserHome;
        if (string.IsNullOrEmpty(home)) return [];

        List<string> paths = [];

        // Credential & key material directories — the highest-risk targets.
        // ~/.ssh (private keys), ~/.aws (CLI credentials), ~/.gnupg (PGP keys),
        // ~/.config/gcloud (GCP), ~/.kube (k8s tokens), ~/.docker (registry creds),
        // ~/.netrc (FTP/API creds), ~/.password-store (pass utility).
        foreach (var sub in new[]
        {
            ".ssh", ".aws", ".gnupg", ".kube", ".docker", ".password-store",
            Path.Combine(".config", "gcloud"),
        })
        {
            AddIfNonEmpty(paths, Path.Combine(home, sub));
        }

        // Personal user directories (XDG-style, mirrors macOS TCC-protected set).
        foreach (var sub in new[]
        {
            "Documents", "Downloads", "Desktop", "Pictures", "Music", "Videos",
        })
        {
            AddIfNonEmpty(paths, Path.Combine(home, sub));
        }

        // Shell history files may contain secrets typed interactively.
        foreach (var f in new[] { ".bash_history", ".zsh_history", ".python_history" })
        {
            var full = Path.Combine(home, f);
            if (File.Exists(full))
                paths.Add(full);
        }

        return paths;
    }

    private static void AddIfNonEmpty(List<string> list, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            list.Add(value);
    }
}
