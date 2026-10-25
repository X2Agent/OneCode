namespace OneCode.Tui;

public static class LaunchModeDetector
{
    public static LaunchMode Detect(string[] args)
    {
        if (args.Length == 0)
            return LaunchMode.Interactive;

        if (IsVersionFlag(args))
            return LaunchMode.FastPathVersion;

        if (string.Equals(args[0], "daemon", StringComparison.OrdinalIgnoreCase))
            return LaunchMode.Daemon;

        return LaunchMode.Interactive;
    }

    private static bool IsVersionFlag(string[] args) =>
        args.Length == 1 && args[0] is "--version" or "-v" or "-V";
}
