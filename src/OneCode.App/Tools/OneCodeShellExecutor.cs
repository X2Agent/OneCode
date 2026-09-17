using Microsoft.Agents.AI.Tools.Shell;
using OneCode.App.Session;
using OneCode.Core.Exec;
using OneCode.Core.IO;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Middleware.Invariants;
using OneCode.Infrastructure.Remote;

namespace OneCode.App.Tools;

/// <summary>OneCode 默认 Shell Provider，封装本地、持久会话和 SSH 执行后端。</summary>
public sealed class OneCodeShellExecutor(
    SshRemoteService ssh,
    ConversationShellExecutorManager shellSessions,
    ISessionConversationAccess sessions,
    IProcessRunner processRunner) : IShellExecutor
{
    private static readonly IReadOnlyList<string> PowerShellDenyPatterns =
        BashCommandInvariant.DenyPatternStrings
            .Concat([
                @"\bSet-ExecutionPolicy\b",
                @"\b(Format-Volume|Clear-Disk)\b",
                @"\b(Restart-Computer|Stop-Computer)\b",
                @"\bStart-Process\b[^|;&\n]*-(Verb|v)(:|\s+)RunAs\b",
            ])
            .ToList()
            .AsReadOnly();

    public async Task<ShellExecutionResult> ExecuteAsync(
        ShellExecutionRequest request,
        CancellationToken ct = default)
    {
        if (SshToolHelper.IsActive(ssh))
        {
            await using var sshExecutor = new SshShellExecutor(ssh, new SshShellExecutorOptions
            {
                Timeout = TimeSpan.FromMilliseconds(ShellExecutionHelper.ClampTimeoutMs(request.TimeoutSeconds)),
                MaxOutputBytes = ShellExecutionHelper.MaxOutputChars,
            });
            return FromMafResult(await sshExecutor.RunAsync(request.Command, ct).ConfigureAwait(false));
        }

        if (request.Shell.Equals("powershell", StringComparison.OrdinalIgnoreCase))
        {
            var hasPwsh = await processRunner.CommandExistsAsync("pwsh").ConfigureAwait(false);
            if (!hasPwsh && !OperatingSystem.IsWindows())
                throw new FileNotFoundException("PowerShell (pwsh/powershell) not found.");

            return await ExecuteStatelessAsync(
                request,
                hasPwsh ? "pwsh" : "powershell",
                PowerShellDenyPatterns,
                ct).ConfigureAwait(false);
        }

        if (request.ConversationId is { } conversationId && sessions.GetConversation(conversationId) is not null)
        {
            var result = await shellSessions.ExecuteAsync(
                conversationId,
                request.WorkingDirectory,
                request.Command,
                TimeSpan.FromMilliseconds(ShellExecutionHelper.ClampTimeoutMs(request.TimeoutSeconds)),
                ct).ConfigureAwait(false);
            return FromMafResult(result);
        }

        return await ExecuteStatelessAsync(
            request,
            shell: null,
            BashCommandInvariant.DenyPatternStrings,
            ct).ConfigureAwait(false);
    }

    private static async Task<ShellExecutionResult> ExecuteStatelessAsync(
        ShellExecutionRequest request,
        string? shell,
        IEnumerable<string> denyPatterns,
        CancellationToken ct)
    {
        await using var executor = new LocalShellExecutor(new LocalShellExecutorOptions
        {
            Mode = ShellMode.Stateless,
            Shell = shell,
            WorkingDirectory = request.WorkingDirectory,
            MaxOutputBytes = ShellExecutionHelper.MaxOutputChars,
            Timeout = TimeSpan.FromMilliseconds(ShellExecutionHelper.ClampTimeoutMs(request.TimeoutSeconds)),
            AcknowledgeUnsafe = true,
            Policy = new ShellPolicy(denyList: denyPatterns),
        });
        return FromMafResult(await executor.RunAsync(request.Command, ct).ConfigureAwait(false));
    }

    private static ShellExecutionResult FromMafResult(ShellResult result) =>
        new(result.Stdout, result.Stderr, result.ExitCode, result.Truncated);
}