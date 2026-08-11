namespace OneCode.Core.Hooks;

/// <summary>
/// Hook 执行服务契约。
/// 从 App 层下沉到 Core 层，使 Infrastructure 层的 AgentPipelineBuilder
/// 可通过此接口调用 Hook，避免反向依赖 App 层。
/// </summary>
public interface IHookExecutionService
{
    Task<AggregatedHookResult> FireAsync(
        HookPayload payload,
        string? actualMatcherValue = null,
        CancellationToken ct = default);
}
