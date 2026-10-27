using OneCode.Core.Domain;

namespace OneCode.Core.Session;

/// <summary>
/// 会话摘要的落盘索引：冷启动时只读小索引文件，避免为拿标题/条数而全量解析事件日志。
/// 索引以事件日志戳（长度 + 最后写入时间）作为唯一有效性凭据，戳不匹配即视为未命中。
/// </summary>
public interface ISessionSummaryIndex
{
    /// <summary>按日志戳读取摘要；索引缺失、损坏或戳不匹配时返回 <c>null</c>。</summary>
    Task<ConversationSummary?> ReadAsync(SessionEventLogStamp stamp, CancellationToken ct = default);

    /// <summary>写入摘要索引，覆盖同会话的旧索引。</summary>
    /// <remarks>
    /// 实现必须吞掉写入失败与取消：索引只是加速层，任何异常都不得让调用方的保存路径失败。
    /// </remarks>
    Task WriteAsync(SessionEventLogStamp stamp, ConversationSummary summary, CancellationToken ct = default);

    /// <summary>删除摘要索引。</summary>
    Task RemoveAsync(SessionId sessionId, CancellationToken ct = default);
}
