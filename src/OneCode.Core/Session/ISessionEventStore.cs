using OneCode.Core.Domain;

namespace OneCode.Core.Session;

/// <summary>
/// 事件日志的轻量元信息（不含内容）：会话列表据此判断摘要缓存是否失效。
/// <see cref="Length"/> 为日志字节数，<see cref="LastWriteTimeUtc"/> 为最后写入时间。
/// </summary>
public readonly record struct SessionEventLogStamp(
    SessionId SessionId,
    long Length,
    DateTimeOffset LastWriteTimeUtc);

/// <summary>会话事件的追加和读取契约。</summary>
public interface ISessionEventStore
{
    /// <summary>
    /// 原子追加一批会话事件，返回追加后的日志戳；没有事件可写时返回 <c>null</c>。
    /// 调用方用该戳回填摘要索引，避免再枚举一次目录。
    /// </summary>
    Task<SessionEventLogStamp?> AppendAsync(
        IReadOnlyList<SessionEvent> events,
        CancellationToken ct = default);

    /// <summary>按序读取指定会话的事件。</summary>
    Task<IReadOnlyList<SessionEvent>> ReadAsync(SessionId sessionId, CancellationToken ct = default);

    /// <summary>列出已有事件日志的会话及其日志戳（只取元信息，不解析内容）。</summary>
    Task<IReadOnlyList<SessionEventLogStamp>> ListLogsAsync(CancellationToken ct = default);

    /// <summary>删除指定会话的事件日志。</summary>
    Task DeleteAsync(SessionId sessionId, CancellationToken ct = default);
}
