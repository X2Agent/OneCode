using OneCode.Core.Domain;
using OneCode.Core.Session;
using OneCode.Core.Tokens;
using OneCode.Infrastructure.Persistence;

namespace OneCode.Infrastructure.Api;

/// <summary>
/// <see cref="ITokenLedger"/> 默认实现：内存中即时累计，后台单写者合并落盘。
/// 预算读取始终看到最新内存值；磁盘只承担跨进程合并与重启水合。
/// </summary>
public sealed class TokenLedger : ITokenLedger, ITokenLedgerFlush, IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan BatchDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WorkerFailureLogInterval = TimeSpan.FromSeconds(10);

    private long _totalTokens;
    private ConcurrentDictionary<SessionId, SessionTokenUsage> _sessionUsage = new();
    private readonly JsonUsageStateStore? _stateStore;
    private readonly ILogger<TokenLedger>? _logger;
    private readonly object _sync = new();
    private readonly Dictionary<SessionId, PendingUsage> _pending = new();
    private PendingUsage? _globalPending;
    private readonly HashSet<SessionId> _deletedSessions = new();
    private readonly SemaphoreSlim _flushSignal = new(0, 1);
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly CancellationTokenSource _workerCts = new();
    private readonly Task? _worker;
    private DateTimeOffset _lastWorkerFailureLog;
    private bool _disposed;

    public TokenLedger(JsonUsageStateStore? stateStore = null, ILogger<TokenLedger>? logger = null)
    {
        _stateStore = stateStore;
        _logger = logger;
        if (stateStore is not null)
        {
            // 水合（跨进程锁 + 读文件）与后台写者一起放到线程池：构造发生在 DI 解析期，
            // 同步阻塞会让「首帧之前」多出一段与文件大小成正比的等待。
            // 读侧在此之前看到的是空账本——与「历史用量尚未恢复」等价，不是错误状态。
            _hydration = Task.Run(() => Hydrate(stateStore));
            _worker = Task.Run(WorkerAsync);
        }
    }

    private readonly Task? _hydration;

    /// <summary>等待首次水合完成（写入路径必须先完成水合，否则会用空账本覆盖历史）。</summary>
    /// <remarks>
    /// 对工程规范「禁止 <c>GetAwaiter().GetResult()</c>」的<b>登记在案的豁免点</b>：
    /// <see cref="ITokenLedger"/> 契约是同步方法，等待无法下放给调用方；调用方
    /// 全部在引擎后台线程（无同步上下文，无死锁面），且只在进程启动后首次记账
    /// /读取的短窗口内可能阻塞。若契约未来异步化，此处应改为直接 await 并删除本豁免。
    /// </remarks>
    private void EnsureHydrated()
    {
        if (_hydration is not { IsCompleted: false } hydration)
            return;

        try
        {
            hydration.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Token usage hydration failed; continuing with in-memory ledger");
        }
    }

    private void Hydrate(JsonUsageStateStore stateStore)
    {
        var persisted = stateStore.Load();
        lock (_sync)
            RebuildMemory(persisted);
    }

    public void RecordUsage(UsageRecord record) => RecordCore(null, record);

    public void RecordUsage(SessionId sessionId, UsageRecord record) => RecordCore(sessionId, record);

    private void RecordCore(SessionId? sessionId, UsageRecord record)
    {
        EnsureHydrated();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (sessionId is { } id && _deletedSessions.Contains(id))
                throw new SessionDeletedException(id);

            AddMemory(sessionId, record);
            if (_stateStore is null)
                return;

            if (sessionId is { } session)
            {
                if (!_pending.TryGetValue(session, out var pending))
                {
                    pending = new PendingUsage();
                    _pending[session] = pending;
                }
                pending.Add(record);
            }
            else
            {
                (_globalPending ??= new PendingUsage()).Add(record);
            }

            // 信号只承担唤醒；计数在 _sync 内检查，连续记账合并到同一后台批次。
            if (_flushSignal.CurrentCount == 0)
                _flushSignal.Release();
        }
    }

    private void AddMemory(SessionId? sessionId, UsageRecord record)
    {
        Interlocked.Add(ref _totalTokens, UsageMath.BillableTokens(record));
        if (sessionId is not { } id)
            return;

        _sessionUsage.AddOrUpdate(
            id,
            _ =>
            {
                var created = new SessionTokenUsage();
                created.Record(record);
                return created;
            },
            (_, existing) =>
            {
                existing.Record(record);
                return existing;
            });
    }

    public SessionTokenUsage? GetSessionUsage(SessionId sessionId)
    {
        EnsureHydrated();
        return Volatile.Read(ref _sessionUsage).TryGetValue(sessionId, out var info) ? info : null;
    }

    public void RemoveSession(SessionId sessionId)
    {
        // 先屏蔽本进程新写入，再 flush：否则 flush 与标记之间的增量会被随后删除而丢失。
        lock (_sync)
            _deletedSessions.Add(sessionId);

        FlushBlocking(Timeout.InfiniteTimeSpan);

        lock (_sync)
        {
            _pending.Remove(sessionId);
            _sessionUsage.TryRemove(sessionId, out _);
        }
        _stateStore?.RemoveSession(sessionId);
    }

    public long GetTotalTokens()
    {
        EnsureHydrated();
        return Interlocked.Read(ref _totalTokens);
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_stateStore is null)
            return;

        // flush 会用磁盘状态重建内存账本：必须等首次水合完成，否则历史用量被判为 0。
        EnsureHydrated();

        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            FlushCore();
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <summary>
    /// 同步入口（<see cref="RemoveSession"/>、<see cref="Dispose"/>）使用的 flush。
    /// <see cref="FlushCore"/> 本身就是同步 I/O（跨进程文件锁 + 原子写），直接取同一把闸门执行即可，
    /// 不经 async 包装再阻塞等待。闸门被后台批次占用超过 <paramref name="timeout"/> 时抛 <see cref="TimeoutException"/>。
    /// </summary>
    private void FlushBlocking(TimeSpan timeout)
    {
        if (_stateStore is null)
            return;

        if (!_flushGate.Wait(timeout))
            throw new TimeoutException("Timed out waiting for the token usage flush gate.");

        try
        {
            FlushCore();
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private void FlushCore()
    {
        List<UsageBatchItem> batch;
        lock (_sync)
        {
            if (_pending.Count == 0 && _globalPending is null)
                return;
            batch = _pending.Select(pair => pair.Value.ToBatchItem(pair.Key)).ToList();
            if (_globalPending is { } global)
                batch.Add(global.ToBatchItem(sessionId: null));
            _pending.Clear();
            _globalPending = null;
        }

        PersistedTokenUsageState persisted;
        try
        {
            persisted = _stateStore!.RecordBatch(batch);
        }
        catch
        {
            lock (_sync)
                RestoreBatch(batch);
            throw;
        }

        lock (_sync)
            RebuildMemory(persisted);
    }

    private void RestoreBatch(IEnumerable<UsageBatchItem> batch)
    {
        foreach (var item in batch)
        {
            if (item.SessionId is null)
            {
                (_globalPending ??= new PendingUsage()).Add(item);
                continue;
            }
            if (_deletedSessions.Contains(item.SessionId.Value))
                continue;
            if (!_pending.TryGetValue(item.SessionId.Value, out var pending))
            {
                pending = new PendingUsage();
                _pending[item.SessionId.Value] = pending;
            }
            pending.Add(item);
        }
    }

    private void RebuildMemory(PersistedTokenUsageState persisted)
    {
        var sessions = new ConcurrentDictionary<SessionId, SessionTokenUsage>();
        foreach (var (sessionId, bucket) in persisted.Sessions)
        {
            if (!_deletedSessions.Contains(sessionId))
                sessions[sessionId] = bucket.Snapshot();
        }

        foreach (var (id, pending) in _pending)
        {
            if (_deletedSessions.Contains(id))
                continue;
            sessions.AddOrUpdate(
                id,
                _ =>
                {
                    var created = new SessionTokenUsage();
                    created.Add(
                        pending.InputTokens,
                        pending.OutputTokens,
                        pending.CacheReadTokens,
                        pending.CacheWriteTokens);
                    return created;
                },
                (_, existing) =>
                {
                    existing.Add(
                        pending.InputTokens,
                        pending.OutputTokens,
                        pending.CacheReadTokens,
                        pending.CacheWriteTokens);
                    return existing;
                });
        }

        Interlocked.Exchange(ref _sessionUsage, sessions);
        var pendingBillable = (_globalPending?.BillableTokens ?? 0)
            + _pending.Values.Sum(item => item.BillableTokens);
        Interlocked.Exchange(ref _totalTokens, persisted.TotalBillableTokens + pendingBillable);
    }

    private async Task WorkerAsync()
    {
        try
        {
            while (true)
            {
                await _flushSignal.WaitAsync(_workerCts.Token).ConfigureAwait(false);
                await Task.Delay(BatchDelay, _workerCts.Token).ConfigureAwait(false);
                try
                {
                    await FlushAsync(_workerCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_workerCts.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogWorkerFailure(ex);
                }
            }
        }
        catch (OperationCanceledException) when (_workerCts.IsCancellationRequested)
        {
        }
    }

    private void LogWorkerFailure(Exception ex)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastWorkerFailureLog < WorkerFailureLogInterval)
            return;
        _lastWorkerFailureLog = now;
        _logger?.LogWarning(ex, "Background token usage flush failed; the batch was restored for a later flush");
    }

    /// <summary>
    /// 同步释放：收尾 flush 走同步闸门；不等待后台 worker——取消后它在下一个 await 点自行退出，
    /// 此时已没有未落盘的数据。
    /// </summary>
    public void Dispose()
    {
        if (!TryBeginDispose())
            return;

        try
        {
            FlushBlocking(DisposeTimeout);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Final token usage flush failed during disposal");
        }

        _workerCts.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (!TryBeginDispose())
            return;

        try
        {
            using var timeout = new CancellationTokenSource(DisposeTimeout);
            await FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Final token usage flush failed during disposal");
        }

        _workerCts.Cancel();
        if (_worker is not null)
        {
            try
            {
                await _worker.WaitAsync(DisposeTimeout).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                _logger?.LogDebug(ex, "Token usage worker did not stop within the disposal budget");
            }
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>首次释放返回 true；没有持久化后端的纯内存账本没有需要收尾的资源。</summary>
    private bool TryBeginDispose()
    {
        if (_stateStore is null)
            return false;

        lock (_sync)
        {
            if (_disposed)
                return false;

            _disposed = true;
            return true;
        }
    }

    private sealed class PendingUsage
    {
        public long InputTokens { get; private set; }
        public long OutputTokens { get; private set; }
        public long CacheReadTokens { get; private set; }
        public long CacheWriteTokens { get; private set; }
        public long BillableTokens { get; private set; }

        public void Add(UsageRecord record)
        {
            InputTokens += record.InputTokens;
            OutputTokens += record.OutputTokens;
            CacheReadTokens += record.CacheReadTokens;
            CacheWriteTokens += record.CacheWriteTokens;
            BillableTokens += UsageMath.BillableTokens(record);
        }

        public void Add(UsageBatchItem item)
        {
            InputTokens += item.InputTokens;
            OutputTokens += item.OutputTokens;
            CacheReadTokens += item.CacheReadTokens;
            CacheWriteTokens += item.CacheWriteTokens;
            BillableTokens += item.BillableTokens;
        }

        public UsageBatchItem ToBatchItem(SessionId? sessionId) => new(
            sessionId,
            InputTokens,
            OutputTokens,
            CacheReadTokens,
            CacheWriteTokens,
            BillableTokens);
    }
}
