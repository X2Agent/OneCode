using OneCode.App.Query;

namespace OneCode.App.Services;

/// <summary>
/// 运行事件旁路出口：供没有流式返回值的路径（如已批准计划的 Build run）把事件送回前端。
/// 每个前端（TUI 一份、每个浏览器电路各一份）独立订阅并在销毁时释放；无订阅者时写入被丢弃。
/// </summary>
public sealed class RunEventBridge(
    ILogger<RunEventBridge>? logger = null)
{
    private readonly Lock _gate = new();
    private Action<QueryEvent>[] _handlers = [];

    /// <summary>注册接收者；返回的令牌必须在销毁时释放，否则事件会继续投递给已关闭的前端。</summary>
    public IDisposable Subscribe(Action<QueryEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_gate)
        {
            _handlers = [.. _handlers, handler];
        }

        return new Subscription(this, handler);
    }

    /// <summary>向当前全部订阅者投递；单个订阅者抛异常不影响其余订阅者与编排流程。</summary>
    public void Publish(QueryEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        foreach (var handler in Volatile.Read(ref _handlers))
        {
            try
            {
                handler(evt);
            }
            catch (Exception ex)
            {
                // 只记事件类型与异常，不记事件载荷（对话正文不得写入日志）。
                logger?.LogWarning(ex, "RunEventBridge 订阅者处理 {EventType} 时抛出异常", evt.GetType().Name);
            }
        }
    }

    private void Unsubscribe(Action<QueryEvent> handler)
    {
        lock (_gate)
        {
            _handlers = [.. _handlers.Where(candidate => !ReferenceEquals(candidate, handler))];
        }
    }

    private sealed class Subscription(RunEventBridge owner, Action<QueryEvent> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Unsubscribe(handler);
        }
    }
}
