using OneCode.Core.Domain;
using OneCode.Core.Tokens;

namespace OneCode.Infrastructure.Api;

/// <summary>
/// <see cref="ITokenLedger"/> 默认实现：进程级累计 + 按会话隔离的 token 账本。
/// </summary>
public sealed class TokenLedger : ITokenLedger
{
    private long _totalTokens;

    private readonly ConcurrentDictionary<SessionId, SessionTokenUsage> _sessionUsage = new();

    public void RecordUsage(UsageRecord record) =>
        // 预算熔断按可计费口径累计（缓存读 10% 折算）——InputTokens 含缓存命中，
        // 全额累计会让长会话的重复上下文计费把预算打穿（见 UsageMath remarks）。
        Interlocked.Add(ref _totalTokens, UsageMath.BillableTokens(record));

    public void RecordUsage(SessionId sessionId, UsageRecord record)
    {
        RecordUsage(record);

        _sessionUsage.AddOrUpdate(
            sessionId,
            _ =>
            {
                var info = new SessionTokenUsage();
                info.Record(record);
                return info;
            },
            (_, existing) =>
            {
                existing.Record(record);
                return existing;
            });
    }

    public SessionTokenUsage? GetSessionUsage(SessionId sessionId) =>
        _sessionUsage.TryGetValue(sessionId, out var info) ? info : null;

    public void RemoveSession(SessionId sessionId) =>
        _sessionUsage.TryRemove(sessionId, out _);

    public long GetTotalTokens() => Interlocked.Read(ref _totalTokens);
}
