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

    private static bool IsVersionFlag(string[] args)
    {
        if (args.Length != 1)
            return false;

        return args[0] is "--version" or "-v" or "-V";
    }
}
