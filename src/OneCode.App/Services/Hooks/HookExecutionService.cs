namespace OneCode.App.Services.Hooks;

/// <summary>
/// Hook 执行服务——应用层统一入口
///
/// 整合 HookRegistry，按 HookType 分发到 IHookExecutor。
/// 执行器经 <see cref="IEnumerable{T}"/> 注入并按 <see cref="IHookExecutor.Type"/> 自建分发字典
/// （照抄 NotificationHookExecutor 的 Provider 分发模式）：新增 HookType 只需实现
/// IHookExecutor 并注册 DI，无需修改本类。
/// </summary>
public sealed class HookExecutionService : IHookExecutionService
{
    private readonly HookRegistry _hookRegistry;
    private readonly Dictionary<HookType, IHookExecutor> _executors;
    private readonly HookPolicyService _policyService;
    private readonly ILogger<HookExecutionService> _logger;

    public HookExecutionService(
        HookRegistry hookRegistry,
        IEnumerable<IHookExecutor> executors,
        HookPolicyService policyService,
        ILogger<HookExecutionService> logger)
    {
        _hookRegistry = hookRegistry ?? throw new ArgumentNullException(nameof(hookRegistry));
        _policyService = policyService ?? throw new ArgumentNullException(nameof(policyService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 同类型重复注册时首个生效（与旧 keyed 注入的 FirstOrDefault 语义一致）
        _executors = executors
            .GroupBy(e => e.Type)
            .ToDictionary(g => g.Key, g => g.First());
    }

    public async Task<AggregatedHookResult> FireAsync(
        HookPayload payload,
        string? actualMatcherValue = null,
        CancellationToken ct = default)
    {
        if (!_policyService.IsCurrentWorkspaceTrusted())
        {
            _logger.LogDebug("Hook execution skipped: workspace not trusted");
            return new AggregatedHookResult();
        }

        var hooks = _hookRegistry.GetMatchesForEvent(payload.Event, actualMatcherValue).ToList();
        if (hooks.Count == 0)
            return new AggregatedHookResult();

        hooks.Sort((a, b) => a.Priority.CompareTo(b.Priority));

        return await ExecuteAndAggregateAsync(hooks, payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 串行执行并聚合结果。<c>once</c> hook 仅在<strong>成功执行</strong>后注销——
    /// 异常 / 取消 / 执行器缺失视为未完成，保留待下次触发；
    /// Blocking（成功送达阻断裁决，如 Stop 阻断触发纠偏续跑）同样移除，防止无限循环。
    /// </summary>
    private async Task<AggregatedHookResult> ExecuteAndAggregateAsync(
        List<HookRegistration> hooks,
        HookPayload payload,
        CancellationToken ct)
    {
        List<HookResult?> results = [];

        foreach (var hook in hooks)
        {
            var result = await ExecuteSingleHookAsync(hook, payload, ct).ConfigureAwait(false);
            results.Add(result);

            if (hook.Once && result is { Outcome: HookOutcome.Success or HookOutcome.Blocking })
                _hookRegistry.Unregister(hook.Name);
        }

        return HookResultAggregator.Aggregate(results);
    }

    private async Task<HookResult?> ExecuteSingleHookAsync(
        HookRegistration hook, HookPayload payload, CancellationToken ct)
    {
        try
        {
            return await ExecuteHookByTypeAsync(
                hook.ExecutorType, payload, hook.Config ?? new HookConfig(), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hook '{Name}' execution error", hook.Name);
            return null;
        }
    }

    private Task<HookResult?> ExecuteHookByTypeAsync(
        HookType type, HookPayload payload, HookConfig config, CancellationToken ct)
    {
        if (_executors.TryGetValue(type, out var executor))
            return executor.ExecuteAsync(payload, config, ct);

        _logger.LogWarning("No executor registered for hook type {Type}", type);
        return Task.FromResult<HookResult?>(null);
    }
}
