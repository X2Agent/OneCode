using System.ComponentModel;
using CliWrap;
using CliWrap.Buffered;
using OneCode.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace OneCode.Infrastructure;

public sealed class ProcessRunner : IProcessRunner
{
    private readonly ConcurrentDictionary<string, bool> _commandExistsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ProcessRunner> _logger;

    public ProcessRunner(ILogger<ProcessRunner> logger)
    {
        _logger = logger;
    }

    public ProcessRunner() : this(NullLoggerFactory.Instance.CreateLogger<ProcessRunner>()) { }

    public async Task<ProcessResult?> ExecuteAsync(
        string command,
        string[] args,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken ct = default)
    {
        return await ExecuteCoreAsync(command, args, workingDirectory, environmentVariables, ct).ConfigureAwait(false);
    }

    public async Task<ProcessResult?> ExecuteWithTimeoutAsync(
        string command,
        string[] args,
        string? workingDirectory = null,
        int timeoutMs = 30_000,
        CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            return await ExecuteCoreAsync(
                command, args, workingDirectory, environmentVariables: null, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // External cancellation — do not mislabel as timeout.
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ProcessResult(
                ExitCode: -1,
                Stdout: string.Empty,
                Stderr: "Process timed out",
                TimedOut: true);
        }
    }

    public async Task WarmCommandCacheAsync(params string[] commands)
    {
        var tasks = commands.Select(async cmd =>
        {
            var exists = await CommandExistsAsyncCore(cmd).ConfigureAwait(false);
            _commandExistsCache[cmd] = exists;
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task<bool> CommandExistsAsync(string command)
    {
        if (_commandExistsCache.TryGetValue(command, out var cached))
            return cached;

        var exists = await CommandExistsAsyncCore(command).ConfigureAwait(false);
        _commandExistsCache[command] = exists;
        return exists;
    }

    public async Task<ProcessResult?> ExecuteWithArgumentListAsync(
        string command,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken ct = default)
    {
        return await ExecuteCoreAsync(command, arguments, workingDirectory, environmentVariables, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Unified core execution. Propagates <see cref="OperationCanceledException"/>
    /// so callers can distinguish cancel vs timeout.
    /// </summary>
    private async Task<ProcessResult?> ExecuteCoreAsync(
        string command,
        IEnumerable<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environmentVariables,
        CancellationToken ct)
    {
        try
        {
            var cli = Cli.Wrap(command)
                .WithArguments(arguments)
                .WithWorkingDirectory(ResolveWorkingDirectory(workingDirectory))
                .WithValidation(CommandResultValidation.None);

            if (environmentVariables != null)
                cli = cli.WithEnvironmentVariables(env =>
                {
                    foreach (var kvp in environmentVariables)
                        env.Set(kvp.Key, kvp.Value);
                });

            var result = await cli.ExecuteBufferedAsync(ct).ConfigureAwait(false);

            return new ProcessResult(
                ExitCode: result.ExitCode,
                Stdout: result.StandardOutput,
                Stderr: result.StandardError,
                TimedOut: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FileNotFoundException or Win32Exception { NativeErrorCode: 2 })
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Process '{Command}' failed with args: {Arguments}", command, arguments);
            throw;
        }
    }

    private static string ResolveWorkingDirectory(string? workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            return workingDirectory;

        var current = Directory.GetCurrentDirectory();
        return Directory.Exists(current) ? current : AppContext.BaseDirectory;
    }

    private async Task<bool> CommandExistsAsyncCore(string command)
    {
        try
        {
            var result = await ExecuteWithTimeoutAsync(command, ["--version"], timeoutMs: 5000).ConfigureAwait(false);
            return result is { ExitCode: 0 };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CommandExists check failed for {Command}", command);
            return false;
        }
    }
}
