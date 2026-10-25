namespace OneCode.App.Services.Interaction;

/// <summary>
/// 前端交互投递通道：把 <see cref="InteractionRequest"/> 送到界面。
/// 界面未就绪（无头 / 电路已断开）时返回 false，端口据此走无头降级。
/// </summary>
public interface IInteractionDispatcher
{
    /// <summary>投递请求；无接收者返回 false。</summary>
    bool TrySend(InteractionRequest request);
}
