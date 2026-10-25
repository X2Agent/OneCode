namespace OneCode.Core.Permissions;

/// <summary>
/// 审批提问端口：把「是否允许执行该工具」呈现给用户并等待决策。
/// App 提供统一默认实现（经 <c>IInteractionDispatcher</c> 投递到前端），
/// 交互式前端只替换投递通道；无头宿主无接收者，投递失败按拒绝降级。
/// </summary>
/// <remarks>
/// 与 <see cref="IApprovalBroker"/> 的分工：broker 拥有 run 级放行策略与异常到决策的映射，
/// 本接口只负责「怎么问用户」。两者分离后，run 级状态仍由每 run 创建的 broker 单点持有。
/// </remarks>
public interface IApprovalPromptSink
{
    /// <summary>呈现审批请求并等待用户决策；实现必须保证取消与异常都不悬挂调用方。</summary>
    Task<ApprovalDecision> AskAsync(ApprovalRequest request, CancellationToken ct = default);
}
