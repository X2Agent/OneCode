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

    /// <summary>
    /// 该拦截点在给定 matcher 值下是否存在会真正执行的活跃 hook。
    /// 与 <see cref="FireAsync"/> 的前置过滤逻辑同源（策略门控 + registry 匹配），
    /// 实时查询不缓存——调用方据此决定是否构造 payload / 收集投影，避免无谓开销。
    /// </summary>
    bool HasActiveHooks(HookInterceptionPoint point, string? actualMatcherValue = null);
}
