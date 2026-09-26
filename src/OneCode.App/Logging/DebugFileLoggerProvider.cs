using System.Text;
using Microsoft.Extensions.Options;
namespace OneCode.App.Logging;

public sealed class DebugFileLoggerProvider(IOptions<DebugLogConfig> config) : ILoggerProvider, IDisposable
{
    internal readonly DebugLogConfig _config = config.Value;
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
        if (!_config.Enabled || level < _config.MinimumLevel)
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
                var path = _config.GetLogFilePath();
                var dir = Path.GetDirectoryName(path);
                if (dir is not null)
                    Directory.CreateDirectory(dir);

                _writer = new StreamWriter(path, append: true, Encoding.UTF8)
                {
                    AutoFlush = true
                };
            }
            catch (Exception ex)
            {
                _writer = null;
                Console.Error.WriteLine($"[DebugLog] Failed to create log file: {ex.Message}");
            }
        }
    }

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
        provider._config.Enabled && logLevel >= provider._config.MinimumLevel;

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
