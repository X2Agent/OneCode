using OneCode.Infrastructure;

namespace OneCode.App.Logging;

public sealed class DebugLogConfig
{
    private readonly Lazy<string> _logFilePath;

    public DebugLogConfig()
    {
        _logFilePath = new Lazy<string>(BuildLogFilePath, isThreadSafe: true);
    }

    public bool Enabled { get; init; }
    public LogLevel MinimumLevel { get; init; } = LogLevel.Debug;
    public bool OutputToConsole { get; init; } = true;
    public bool OutputToFile { get; init; } = true;
    public string? DebugLogDirectory { get; init; }
    public string? SessionId { get; init; }

    public static bool DebugBuild
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }

    public static DebugLogConfig Disabled => new() { Enabled = false };

    /// <summary>
    /// Resolves debug-logging config from the single <c>ONECODE_LOG_LEVEL</c> env value.
    /// <list type="bullet">
    /// <item>empty / unknown → build default (DEBUG build: Debug level; otherwise disabled)</item>
    /// <item>off / 0 / false → disabled, even in a DEBUG build</item>
    /// <item>debug / 1 → enabled at <see cref="LogLevel.Debug"/> (works in Release too)</item>
    /// <item>trace / 2 → enabled at <see cref="LogLevel.Trace"/> (implies debug, works in Release too)</item>
    /// </list>
    /// This collapses the previous two bools (ONECODE_DEBUG + ONECODE_VERBOSE) into one
    /// three-state value so neither knob can be silently ineffective (e.g. verbose-without-debug,
    /// or debug-can't-be-turned-off-in-a-DEBUG-build).
    /// </summary>
    internal static DebugLogConfig Resolve(bool isDebugBuild, string? levelEnv)
    {
        if (string.IsNullOrWhiteSpace(levelEnv))
            return BuildDefault(isDebugBuild);

        var value = levelEnv.Trim();
        if (IsOff(value))
            return Disabled;

        if (IsTrace(value))
            return EnabledAt(LogLevel.Trace);

        if (IsDebug(value))
            return EnabledAt(LogLevel.Debug);

        return BuildDefault(isDebugBuild);
    }

    private static DebugLogConfig BuildDefault(bool isDebugBuild)
        => isDebugBuild ? EnabledAt(LogLevel.Debug) : Disabled;

    private static DebugLogConfig EnabledAt(LogLevel level)
        => new()
        {
            Enabled = true,
            MinimumLevel = level,
            OutputToConsole = true,
            OutputToFile = true,
        };

    private static bool IsOff(string v)
        => v is "0" || EqualsIgnoreCase(v, "off") || EqualsIgnoreCase(v, "false");

    private static bool IsTrace(string v)
        => v is "2" || EqualsIgnoreCase(v, "trace");

    private static bool IsDebug(string v)
        => v is "1" || EqualsIgnoreCase(v, "debug");

    private static bool EqualsIgnoreCase(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public string GetLogFilePath()
        => _logFilePath.Value;

    private string BuildLogFilePath()
    {
        var dir = DebugLogDirectory
            ?? Path.Combine(PathsHelper.GetUserConfigDir(), "debug");

        Directory.CreateDirectory(dir);

        var sessionId = SessionId ?? Guid.NewGuid().ToString("N")[..8];
        return Path.Combine(dir, $"{sessionId}.log");
    }
}
