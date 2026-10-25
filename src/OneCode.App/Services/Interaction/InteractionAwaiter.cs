namespace OneCode.App.Services.Interaction;

/// <summary>
/// 交互请求等待模板：投递 → 无接收者降级 → 注册取消 → 等待响应槽。
/// 三个交互端口实现共用；取消异常向上传播，由调用方决定降级语义。
/// </summary>
internal static class InteractionAwaiter
{
    /// <summary>
    /// 投递交互请求并等待响应；无接收者返回 <paramref name="noReceiver"/>，
    /// 取消时响应槽置为取消态并抛出 <see cref="OperationCanceledException"/>。
    /// </summary>
    public static async Task<TResponse> WaitAsync<TResponse>(
        IInteractionDispatcher dispatcher,
        InteractionRequest request,
        TaskCompletionSource<TResponse> responseSource,
        TResponse noReceiver,
        CancellationToken ct)
    {
        if (!dispatcher.TrySend(request))
            return noReceiver;

        using var registration = ct.Register(() => responseSource.TrySetCanceled(ct));
        return await responseSource.Task.ConfigureAwait(false);
    }
}
