namespace OneCode.App.Services.Interaction;

/// <summary>
/// 交互投递通道基类：维护单一接收者，注册与投递均经锁保护（前端跨线程注册/注销安全）。
/// 无接收者时 <see cref="TrySend"/> 返回 false，端口实现据此走无头降级。
/// </summary>
public abstract class InteractionDispatcherBase : IInteractionDispatcher
{
    private readonly object _gate = new();
    private Action<InteractionRequest>? _receiver;

    /// <summary>注册接收者；传 null 注销。</summary>
    public void SetReceiver(Action<InteractionRequest>? receiver)
    {
        lock (_gate)
            _receiver = receiver;
    }

    /// <summary>投递交互请求；无接收者时不投递并返回 false。</summary>
    public bool TrySend(InteractionRequest request)
    {
        Action<InteractionRequest>? receiver;
        lock (_gate)
            receiver = _receiver;

        if (receiver is null)
            return false;

        receiver(request);
        return true;
    }
}