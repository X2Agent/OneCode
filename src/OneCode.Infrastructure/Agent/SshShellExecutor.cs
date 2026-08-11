using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;
using OneCode.Infrastructure.Remote;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// Options for <see cref="SshShellExecutor"/>.
/// </summary>
public sealed class SshShellExecutorOptions
{
    public TimeSpan? Timeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxOutputBytes { get; set; } = 64 * 1024;
}

/// <summary>
/// Executes shell commands on a remote host via SSH.
/// Wraps <see cref="SshRemoteService"/> as a MAF <see cref="ShellExecutor"/>.
/// </summary>
public sealed class SshShellExecutor : ShellExecutor
{
    private readonly SshRemoteService _ssh;
    private readonly SshShellExecutorOptions _options;

    public SshShellExecutor(SshRemoteService ssh, SshShellExecutorOptions? options = null)
    {
        _ssh = ssh;
        _options = options ?? new SshShellExecutorOptions();
    }

    public override async Task<ShellResult> RunAsync(string command, CancellationToken cancellationToken = default)
    {
        var timeoutMs = _options.Timeout.HasValue
            ? (int)_options.Timeout.Value.TotalMilliseconds
            : 30_000;

        var workingDir = _ssh.Config?.EffectiveWorkingDirectory;
        var result = await _ssh.ExecuteCommandAsync(command, workingDir, timeoutMs, cancellationToken)
            .ConfigureAwait(false);

        var stdout = result.StandardOutput ?? "";
        var stderr = result.StandardError ?? "";
        var truncated = false;

        if (_options.MaxOutputBytes > 0 && stdout.Length > _options.MaxOutputBytes)
        {
            stdout = TruncateHeadTail(stdout, _options.MaxOutputBytes);
            truncated = true;
        }

        return new ShellResult(stdout, stderr, result.ExitCode, result.Duration, truncated, false);
    }

    public override AIFunction AsAIFunction(string name = "run_shell", string? description = null, bool requireApproval = true)
    {
        description ??= "Execute a shell command on a remote host via SSH.";
        return AIFunctionFactory.Create(
            async (string command, CancellationToken ct) =>
            {
                try
                {
                    var result = await RunAsync(command, ct).ConfigureAwait(false);
                    return result.FormatForModel();
                }
                catch (ShellCommandRejectedException ex)
                {
                    return ex.Message;
                }
            },
            new AIFunctionFactoryOptions { Name = name, Description = description });
    }

    public override ValueTask DisposeAsync()
    {
        // SshRemoteService is managed by DI; do not dispose here.
        return ValueTask.CompletedTask;
    }

    private static string TruncateHeadTail(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            return value;

        const string marker = "\n...[truncated]...\n";
        var budget = Math.Max(0, maxChars - marker.Length);
        var headLen = budget / 2;
        var tailLen = budget - headLen;
        return value[..headLen] + marker + value[^tailLen..];
    }
}
