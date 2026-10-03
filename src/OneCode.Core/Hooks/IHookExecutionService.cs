namespace OneCode.Core.Hooks;

/// <summary>
/// Hook 执行服务契约，供 Infrastructure 层的 Agent 管线调用 Hook
/// （Core 层契约，避免 Infrastructure 反向依赖 App）。
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
