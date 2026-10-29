using OneCode.Core.Domain;

namespace OneCode.Core.Session;

/// <summary>
/// 会话已被删除的专用控制信号：存储在写入前发现删除标记时抛出，
/// 运行边界据此按 Cancelled 收口，而不是误报为普通执行失败。
/// </summary>
public sealed class SessionDeletedException(SessionId sessionId)
    : InvalidOperationException($"Session '{sessionId}' has been deleted and cannot be written again.")
{
    /// <summary>被删除的会话。</summary>
    public SessionId SessionId { get; } = sessionId;
}
