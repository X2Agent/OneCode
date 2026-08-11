using System.Text;
using Microsoft.Extensions.Options;
namespace OneCode.App.Logging;

public sealed class DebugFileLoggerProvider : ILoggerProvider, IDisposable
{
    internal readonly DebugLogConfig _config;
    private readonly ConcurrentDictionary<string, DebugFileLogger> _loggers = new();
    private StreamWriter? _writer;
    private readonly object _lock = new();

    public DebugFileLoggerProvider(IOptions<DebugLogConfig> config)
    {
        _config = config.Value;
    }

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
        if (_writer == null) return;

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
        if (_writer != null) return;

        lock (_lock)
        {
            if (_writer != null) return;

            try
            {
                var path = _config.GetLogFilePath();
                var dir = Path.GetDirectoryName(path);
                if (dir != null)
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

internal sealed class DebugFileLogger : ILogger
{
    private readonly string _category;
    private readonly DebugFileLoggerProvider _provider;

    public DebugFileLogger(string category, DebugFileLoggerProvider provider)
    {
        _category = category;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) =>
        _provider._config.Enabled && logLevel >= _provider._config.MinimumLevel;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (formatter == null) return;

        var message = formatter(state, exception);
        if (exception != null)
            message += Environment.NewLine + exception;

        _provider.Write(_category, logLevel, message);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
