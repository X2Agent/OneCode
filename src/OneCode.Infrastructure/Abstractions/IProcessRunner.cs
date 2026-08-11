namespace OneCode.Infrastructure.Abstractions;

public interface IProcessRunner
{
    Task<ProcessResult?> ExecuteAsync(
        string command,
        string[] args,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken ct = default);

    /// <summary>
    /// Execute a child process with a hard timeout. External <paramref name="ct"/>
    /// cancellation propagates as <see cref="OperationCanceledException"/>;
    /// timeout alone returns a result with <c>TimedOut: true</c>.
    /// </summary>
    Task<ProcessResult?> ExecuteWithTimeoutAsync(
        string command,
        string[] args,
        string? workingDirectory = null,
        int timeoutMs = 30_000,
        CancellationToken ct = default);

    Task<bool> CommandExistsAsync(string command);

    Task WarmCommandCacheAsync(params string[] commands);

    Task<ProcessResult?> ExecuteWithArgumentListAsync(
        string command,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken ct = default);
}
