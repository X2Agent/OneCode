using System.Text;
using OneCode.Infrastructure;

namespace OneCode.App.Logging;

/// <summary>
/// 调试文件日志通道——按会话写 <c>{configDir}/debug/{sessionId}.log</c>。
/// 最低级别唯一来源是构建模式：DEBUG 构建记录 <see cref="LogLevel.Debug"/> 及以上，
/// Release 构建只记录 <see cref="LogLevel.Warning"/> 及以上（保留现场取证、不产生 Info 噪声），
/// 没有运行时开关。控制台通道的级别与样式同样由构建模式决定（见 LoggingServiceCollectionExtensions）。
/// </summary>
public sealed class DebugFileLoggerProvider : ILoggerProvider, IDisposable
{
    public static LogLevel MinimumLevel { get; } =
#if DEBUG
        LogLevel.Debug;
#else
        LogLevel.Warning;
#endif

    public static bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    private static readonly Lazy<string> LogFilePath = new(BuildLogFilePath, isThreadSafe: true);

    private readonly ConcurrentDictionary<string, DebugFileLogger> _loggers = new();
    private StreamWriter? _writer;
    private readonly object _lock = new();

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name =>
            new DebugFileLogger(name, this));
    }

    internal void Write(string categoryName, LogLevel level, string message)
    {
        if (level < MinimumLevel)
            return;

        EnsureWriter();
        if (_writer is null) return;

        var entry = FormatEntry(categoryName, level, message);

        lock (_lock)
        {
            _writer.Write(entry);
            _writer.Flush();
        }
    }

    private string FormatEntry(string category, LogLevel level, string message)
    {
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        sb.Append(" [");
        sb.Append(level.ToString().ToUpperInvariant());
        sb.Append("] [");
        sb.Append(category);
        sb.Append("] ");
        sb.AppendLine(message);
        return sb.ToString();
    }

    private void EnsureWriter()
    {
        if (_writer is not null) return;

        lock (_lock)
        {
            if (_writer is not null) return;

            try
            {
                var path = LogFilePath.Value;
                var dir = Path.GetDirectoryName(path);
                if (dir is not null)
                    Directory.CreateDirectory(dir);

                _writer = new StreamWriter(path, append: true, Encoding.UTF8)
                {
                    AutoFlush = true
                };

                // 保留上限：每进程一个 {sessionId}.log 且无轮转，不清扫会无限堆积。
                // 后台清 7 天前的旧日志；并发占用等失败留给下一次启动。
                if (dir is { } sweepDir)
                    _ = Task.Run(() => SweepOldLogs(sweepDir));
            }
            catch (Exception ex)
            {
                _writer = null;
                Console.Error.WriteLine($"[DebugLog] Failed to create log file: {ex.Message}");
            }
        }
    }

    private static void SweepOldLogs(string dir)
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(7);
            foreach (var file in Directory.EnumerateFiles(dir, "*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DebugLog] Log sweep failed: {ex.Message}");
        }
    }

    private static string BuildLogFilePath()
    {
        var dir = Path.Combine(PathsHelper.GetUserConfigDir(), "debug");
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        return Path.Combine(dir, $"{sessionId}.log");
    }

    /// <summary>当前会话的日志文件路径（DEBUG 构建启动提示用）。</summary>
    public static string GetLogFilePath() => LogFilePath.Value;

    public void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
        _loggers.Clear();
    }
}

internal sealed class DebugFileLogger(string category, DebugFileLoggerProvider provider) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) =>
        logLevel >= DebugFileLoggerProvider.MinimumLevel;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (formatter is null) return;

        var message = formatter(state, exception);
        if (exception is not null)
            message += Environment.NewLine + exception;

        provider.Write(category, logLevel, message);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
