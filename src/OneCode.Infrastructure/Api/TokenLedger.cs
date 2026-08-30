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
        Interlocked.Add(ref _totalTokens, (long)record.InputTokens + record.OutputTokens);

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
