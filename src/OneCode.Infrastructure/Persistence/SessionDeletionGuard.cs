using OneCode.Core.Domain;

namespace OneCode.Infrastructure.Persistence;

/// <summary>会话级删除标记：与各存储的写锁配合，阻止已删除会话被迟到写入重新创建。</summary>
public static class SessionDeletionGuard
{
    /// <summary>写入前检查删除标记；已删除会话必须停止后续持久化。</summary>
    public static void ThrowIfDeleted(string dataDirectory, SessionId sessionId)
    {
        if (File.Exists(GetMarkerPath(dataDirectory, sessionId)))
            throw new InvalidOperationException($"Session '{sessionId}' has been deleted and cannot be written again.");
    }

    /// <summary>判断会话是否已删除，供可丢弃的摘要投影安静跳过。</summary>
    public static bool IsDeleted(string dataDirectory, SessionId sessionId)
        => File.Exists(GetMarkerPath(dataDirectory, sessionId));

    /// <summary>创建幂等删除标记。标记不属于业务数据，会话 ID 不得复用。</summary>
    public static void MarkDeleted(string dataDirectory, SessionId sessionId)
    {
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(GetMarkerPath(dataDirectory, sessionId), DateTimeOffset.UtcNow.ToString("O"));
    }

    private static string GetMarkerPath(string dataDirectory, SessionId sessionId)
        => Path.Combine(dataDirectory, $".{sessionId.Value}.deleted");
}
