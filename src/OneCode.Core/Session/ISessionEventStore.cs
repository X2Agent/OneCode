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

    /// <summary>
    /// 只取单个会话的日志戳（不解析内容）。调用方据此判断进程内投影缓存是否仍然有效，
    /// 避免为了一次有效性检查而重读整份日志。
    /// </summary>
    Task<SessionEventLogStamp?> GetLogStampAsync(SessionId sessionId, CancellationToken ct = default);

    /// <summary>列出已有事件日志的会话及其日志戳（只取元信息，不解析内容）。</summary>
    Task<IReadOnlyList<SessionEventLogStamp>> ListLogsAsync(CancellationToken ct = default);

    /// <summary>删除指定会话的事件日志。</summary>
    Task DeleteAsync(SessionId sessionId, CancellationToken ct = default);

    /// <summary>
    /// 以给定事件整体重写会话日志（快照压缩用）：临时文件原子替换，序列号重排为 1..N。
    /// 仅在当前日志戳仍等于 <paramref name="expectedStamp"/> 时替换；已有并发追加时返回
    /// <c>null</c>，调用方必须保留新事件并放弃本轮压缩。
    /// </summary>
    Task<SessionEventLogStamp?> RewriteAsync(
        SessionId sessionId,
        IReadOnlyList<SessionEvent> events,
        SessionEventLogStamp expectedStamp,
        CancellationToken ct = default);
}
