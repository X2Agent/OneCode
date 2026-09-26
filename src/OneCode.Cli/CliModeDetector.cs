namespace OneCode.Cli;

public static class CliModeDetector
{
    public static CliMode Detect(string[] args)
    {
        if (args.Length == 0)
            return CliMode.FullCli;

        if (IsVersionFlag(args))
            return CliMode.FastPathVersion;

        return CliMode.FullCli;
    }

    private static bool IsVersionFlag(string[] args) =>
        args.Length == 1 && args[0] is "--version" or "-v" or "-V";
}
