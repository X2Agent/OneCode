using Microsoft.Agents.AI.Tools.Shell;
using OneCode.App.Session;
using OneCode.Infrastructure.Middleware.Invariants;

namespace OneCode.App.Tools;

/// <summary>
/// Owns one persistent <see cref="LocalShellExecutor"/> per conversation session.
/// </summary>
public sealed class ConversationShellExecutorManager : IShellExecutorCleanup, IAsyncDisposable
{
    private readonly ConcurrentDictionary<SessionId, LocalShellExecutor> _executors = new();
    private readonly ILogger<ConversationShellExecutorManager> _logger;

    /// <summary>
    /// MAF ShellPolicy built from <see cref="BashCommandInvariant.DenyPatternStrings"/>.
    /// Provides executor-level defense-in-depth: even if a code path bypasses the
    /// Pipeline invariant layer, the executor itself will reject dangerous commands.
    /// </summary>
    private static readonly ShellPolicy SharedPolicy = new(denyList: BashCommandInvariant.DenyPatternStrings);

    public ConversationShellExecutorManager(ILogger<ConversationShellExecutorManager> logger)
        => _logger = logger;

    public LocalShellExecutor GetOrCreate(SessionId conversationId, string workingDirectory)
    {
        return _executors.GetOrAdd(conversationId, _ => CreateExecutor(workingDirectory));
    }

    public LocalShellExecutor? TryGet(SessionId conversationId) =>
        _executors.TryGetValue(conversationId, out var executor) ? executor : null;

    public LocalShellExecutor? TryGetForeground(SessionManager sessionManager) =>
        sessionManager.ForegroundConversation is { } conv
            ? TryGet(conv.Id)
            : null;

    public async Task<ShellResult> ExecuteAsync(
        SessionId conversationId,
        string workingDirectory,
        string command,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var executor = GetOrCreate(conversationId, workingDirectory);
        await executor.InitializeAsync(ct).ConfigureAwait(false);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        return await executor.RunAsync(command, timeoutCts.Token).ConfigureAwait(false);
    }

    public async Task ReleaseAsync(SessionId conversationId)
    {
        if (!_executors.TryRemove(conversationId, out var executor))
            return;

        // DisposeAsync closes the underlying shell process. All call sites are async
        // (SessionManager.CloseAsync / CloseBackgroundSessionAsync), so we can await
        // directly without synchronous blocking.
        try
        {
            await executor.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to dispose shell executor for conversation {ConversationId}", conversationId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _executors.Keys.ToArray())
        {
            if (_executors.TryRemove(id, out var executor))
                await executor.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static LocalShellExecutor CreateExecutor(string workingDirectory) =>
        new(new LocalShellExecutorOptions
        {
            Mode = ShellMode.Persistent,
            WorkingDirectory = workingDirectory,
            ConfineWorkingDirectory = false,
            MaxOutputBytes = ShellExecutionHelper.MaxOutputChars,
            Timeout = TimeSpan.FromSeconds(ShellExecutionHelper.MaxTimeoutMs / 1000),
            AcknowledgeUnsafe = true,
            Policy = SharedPolicy,
        });
}
