using OneCode.App.Services.Interaction;

namespace OneCode.Tui;

/// <summary>
/// TUI 交互投递通道：把引擎经 Core 端口发起的请求送到界面线程。
/// 接收者注册与投递语义由 <see cref="InteractionDispatcherBase"/> 统一提供。
/// </summary>
public sealed class TuiInteractionDispatcher : InteractionDispatcherBase;
