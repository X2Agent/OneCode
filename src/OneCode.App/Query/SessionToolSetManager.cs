using OneCode.App.Tools;

namespace OneCode.App.Query;

/// <summary>
/// 环境上下文——通过 <see cref="AsyncLocal{T}"/> 在异步调用链中传递当前会话 ID，
/// 使 <see cref="ToolSearchTool"/> 等无状态工具能在运行时感知当前会话。
/// </summary>
/// <remarks>
/// Session-scoped ambient bridge (not a DI defect): set by <c>ChatService</c> at run start
/// and read by activation tools that lack an explicit conversationId parameter.
/// Prefer migrating to MAF FunctionInvocationContext when the pipeline can carry it.
/// </remarks>
public static class ToolActivationContext
{
    private static readonly AsyncLocal<string?> _conversationId = new();
    private static readonly AsyncLocal<ToolCapabilitySet?> _capabilities = new();

    /// <summary>当前会话 ID（由 <see cref="ChatService"/> 在每次 run 开始时设置）。</summary>
    public static string? CurrentConversationId
    {
        get => _conversationId.Value;
        set => _conversationId.Value = value;
    }

    /// <summary>当前 run 的不可变工具能力边界。</summary>
    public static ToolCapabilitySet? CurrentCapabilities
    {
        get => _capabilities.Value;
        set => _capabilities.Value = value;
    }
}

/// <summary>
/// 会话级工具集管理器——单例，按 conversationId 持有 <see cref="SessionToolSet"/>。
/// 由 <see cref="ChatService"/> 与 <see cref="ToolSearchTool"/> 共用；会话关闭时须调用 <see cref="Remove"/>。
/// </summary>
public sealed class SessionToolSetManager : ISessionToolSetManager
{
    private readonly IToolCatalog _catalog;
    private readonly ToolMetadataRegistry _metadata;
    private readonly ConcurrentDictionary<string, SessionToolSet> _sessions = new(StringComparer.Ordinal);

    public SessionToolSetManager(IToolCatalog catalog, ToolMetadataRegistry metadata)
    {
        _catalog = catalog;
        _metadata = metadata;
    }

    /// <summary>获取或创建指定会话的 <see cref="SessionToolSet"/>。</summary>
    public SessionToolSet GetOrCreate(string conversationId)
        => _sessions.GetOrAdd(conversationId, id => new SessionToolSet(_catalog, _metadata));

    /// <summary>移除会话工具激活态，防止关闭后泄漏。</summary>
    public bool Remove(string conversationId)
        => _sessions.TryRemove(conversationId, out _);

    /// <summary>
    /// 尝试在当前会话中激活一个工具（链路二/三）。
    /// 使用 <see cref="ToolActivationContext.CurrentConversationId"/> 定位会话。
    /// </summary>
    /// <returns>true 表示新激活；false 表示已激活、会话不存在或无环境上下文。</returns>
    public bool TryActivate(string toolName)
    {
        var convId = ToolActivationContext.CurrentConversationId;
        if (convId is null)
            return false;

        if (!_sessions.TryGetValue(convId, out var session))
            return false;

        var capabilities = ToolActivationContext.CurrentCapabilities;
        return capabilities is not null && session.Activate(toolName, capabilities);
    }

    /// <summary>检查指定工具是否在当前会话中已激活。</summary>
    public bool IsActivated(string toolName)
    {
        var convId = ToolActivationContext.CurrentConversationId;
        if (convId is null)
            return false;

        if (!_sessions.TryGetValue(convId, out var session))
            return false;

        return session.ActivatedNames.Contains(toolName);
    }
}
