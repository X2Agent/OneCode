namespace OneCode.App.Services.Interaction;

/// <summary>
/// 无头宿主的交互投递通道：从不注册接收者，<see cref="IInteractionDispatcher.TrySend"/> 恒返回 false。
/// 三个 Core 端口的交互式实现据此走无头降级（审批拒绝 / 提问空结果 / 预算暂停）；
/// 交互式前端在各自注册段用 <c>Replace</c> 覆盖 <see cref="IInteractionDispatcher"/>。
/// </summary>
public sealed class HeadlessInteractionDispatcher : InteractionDispatcherBase
{
}
