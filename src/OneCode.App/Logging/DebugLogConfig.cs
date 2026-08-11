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
