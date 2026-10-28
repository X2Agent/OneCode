using OneCode.Core.Domain;
using OneCode.Core.Tokens;
using OneCode.Infrastructure.Persistence;

namespace OneCode.Infrastructure.Api;

/// <summary>
/// <see cref="ITokenLedger"/> 默认实现：进程级累计 + 按会话隔离的 token 账本。
/// 配置持久化后，每次记录在跨进程事务中追加到最新状态，再用完整结果刷新本地快照。
/// </summary>
public sealed class TokenLedger : ITokenLedger
{
    private long _totalTokens;
    private ConcurrentDictionary<SessionId, SessionTokenUsage> _sessionUsage = new();
    private readonly JsonUsageStateStore? _stateStore;
    private readonly object _sync = new();

    public TokenLedger(JsonUsageStateStore? stateStore = null)
    {
        _stateStore = stateStore;
        if (stateStore is not null)
            Hydrate(stateStore.Load());
    }

    public void RecordUsage(UsageRecord record)
    {
        lock (_sync)
        {
            if (_stateStore is not null)
                Hydrate(_stateStore.RecordUsage(sessionId: null, record));
            else
                AddBillable(record);
        }
    }

    public void RecordUsage(SessionId sessionId, UsageRecord record)
    {
        lock (_sync)
        {
            if (_stateStore is not null)
            {
                Hydrate(_stateStore.RecordUsage(sessionId, record));
                return;
            }

            AddBillable(record);
            _sessionUsage.AddOrUpdate(
                sessionId,
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
    }

    public SessionTokenUsage? GetSessionUsage(SessionId sessionId)
        => Volatile.Read(ref _sessionUsage).TryGetValue(sessionId, out var info) ? info : null;

    public void RemoveSession(SessionId sessionId)
    {
        lock (_sync)
        {
            if (_stateStore is not null)
                Hydrate(_stateStore.RemoveSession(sessionId));
            else
                _sessionUsage.TryRemove(sessionId, out _);
        }
    }

    public long GetTotalTokens() => Interlocked.Read(ref _totalTokens);

    private void AddBillable(UsageRecord record)
        // 输入已含缓存命中；缓存读仅按折算部分计入预算，缓存写入全额计入。
        => Interlocked.Add(ref _totalTokens, UsageMath.BillableTokens(record));

    private void Hydrate(PersistedTokenUsageState state)
    {
        var sessions = new ConcurrentDictionary<SessionId, SessionTokenUsage>();
        foreach (var (sessionId, bucket) in state.Sessions)
            sessions[sessionId] = bucket.Snapshot();
        Interlocked.Exchange(ref _sessionUsage, sessions);
        Interlocked.Exchange(ref _totalTokens, state.TotalBillableTokens);
    }
}
