using Microsoft.Agents.AI.Tools.Shell;
using OneCode.Infrastructure.Middleware.Invariants;

namespace OneCode.App.Tools;

/// <summary>
/// Owns one persistent <see cref="LocalShellExecutor"/> per conversation session.
///
/// 并发契约：<see cref="ReleaseAsync"/> 会释放 MAF <c>ShellSession</c> 内部的同步原语，
/// 若此时仍有在途命令，<c>RunAsync</c> 会抛出 <see cref="ObjectDisposedException"/> 并穿透到调用方。
/// 因此每个会话携带一把 <see cref="SessionShell.Gate"/>，让关闭动作等待在途命令跑完；
/// 不同会话之间互不影响。
/// </summary>
public sealed class ConversationShellExecutorManager : IShellExecutorCleanup, IAsyncDisposable
{
    private readonly ConcurrentDictionary<SessionId, Lazy<SessionShell>> _sessions = new();
    private readonly ILogger<ConversationShellExecutorManager> _logger;

    /// <summary>
    /// MAF ShellPolicy built from <see cref="BashCommandInvariant.DenyPatternStrings"/>.
    /// Provides executor-level defense-in-depth: even if a code path bypasses the
    /// Pipeline invariant layer, the executor itself will reject dangerous commands.
    /// </summary>
    private static readonly ShellPolicy SharedPolicy = new(denyList: BashCommandInvariant.DenyPatternStrings);

    public ConversationShellExecutorManager(ILogger<ConversationShellExecutorManager> logger)
        => _logger = logger;

    /// <summary>
    /// Returns the existing executor for <paramref name="conversationId"/> without creating one.
    /// The executor is handed out outside the session gate — callers must treat it as a
    /// read-only snapshot, not as a licence to run commands concurrently.
    /// </summary>
    public LocalShellExecutor? TryGet(SessionId conversationId) =>
        _sessions.TryGetValue(conversationId, out var lazy) && lazy.IsValueCreated
            ? lazy.Value.Executor
            : null;

    public async Task<ShellResult> ExecuteAsync(
        SessionId conversationId,
        string workingDirectory,
        string command,
        TimeSpan timeout,
        CancellationToken ct)
    {
        while (true)
        {
            var session = GetOrCreate(conversationId, workingDirectory);

            await session.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // ReleaseAsync 在持有 gate 时置位 Released 并同时完成字典驱逐，
                // 因此这里观察到已释放的会话，说明它已被移除——重取新会话即可。
                if (session.Released)
                    continue;

                await session.Executor.InitializeAsync(ct).ConfigureAwait(false);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout);
                return await session.Executor.RunAsync(command, timeoutCts.Token).ConfigureAwait(false);
            }
            finally
            {
                session.Gate.Release();
            }
        }
    }

    public async Task ReleaseAsync(SessionId conversationId)
    {
        if (!_sessions.TryGetValue(conversationId, out var lazy) || !lazy.IsValueCreated)
            return;

        var session = lazy.Value;

        // 先取 gate，等在途命令跑完再拆进程；否则 RunAsync 会命中已释放的同步原语。
        await session.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            session.Released = true;
            // 只驱逐我们持锁的这一实例——并发的 ExecuteAsync 可能已为同一会话装入替代者。
            _sessions.TryRemove(new KeyValuePair<SessionId, Lazy<SessionShell>>(conversationId, lazy));
        }
        finally
        {
            session.Gate.Release();
        }

        // DisposeAsync closes the underlying shell process. All call sites are async
        // (SessionManager.CloseAsync / CloseBackgroundSessionAsync), so we can await
        // directly without synchronous blocking.
        try
        {
            await session.Executor.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to dispose shell executor for conversation {ConversationId}", conversationId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToArray())
        {
            if (_sessions.TryRemove(id, out var lazy) && lazy.IsValueCreated)
                await lazy.Value.Executor.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 通过 <see cref="Lazy{T}"/> 包装，保证每个会话的执行器**至多构造一次**：
    /// <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey,Func{TKey,TValue})"/> 在竞争下
    /// 可能多次调用工厂，直接返回执行器会让落败的那次被静默遗弃且永不释放。
    /// </summary>
    private SessionShell GetOrCreate(SessionId conversationId, string workingDirectory) =>
        _sessions.GetOrAdd(
            conversationId,
            _ => new Lazy<SessionShell>(
                () => new SessionShell(CreateExecutor(workingDirectory)),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

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

    /// <summary>单个会话的 shell 进程，以及串行化其使用的闸门。</summary>
    private sealed class SessionShell(LocalShellExecutor executor)
    {
        public LocalShellExecutor Executor { get; } = executor;

        /// <summary>串行化命令执行与释放动作，避免释放期间仍有命令在途。</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>在持有 <see cref="Gate"/> 时置位，标记该会话正在被拆除。</summary>
        public bool Released { get; set; }
    }
}
