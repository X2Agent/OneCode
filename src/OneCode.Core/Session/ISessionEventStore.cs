using OneCode.Core.Domain;

namespace OneCode.Core.Session;

/// <summary>会话事件的追加和读取契约。</summary>
public interface ISessionEventStore
{
    /// <summary>原子追加一批会话事件并返回分配后的连续序号。</summary>
    Task<IReadOnlyList<long>> AppendAsync(
        IReadOnlyList<SessionEvent> events,
        CancellationToken ct = default);

    /// <summary>按序读取指定会话的事件。</summary>
    Task<IReadOnlyList<SessionEvent>> ReadAsync(SessionId sessionId, CancellationToken ct = default);

    /// <summary>列出已有事件日志的会话 ID。</summary>
    Task<IReadOnlyList<SessionId>> ListSessionsAsync(CancellationToken ct = default);

    /// <summary>删除指定会话的事件日志。</summary>
    Task DeleteAsync(SessionId sessionId, CancellationToken ct = default);
}