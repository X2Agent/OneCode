using Microsoft.Extensions.DependencyInjection;

namespace OneCode.App.Services.Hooks;

/// <summary>
/// Hook 执行服务——应用层统一入口
///
/// 整合 HookRegistry，按 HookType 分发到 IHookExecutor。
/// </summary>
public sealed class HookExecutionService : IHookExecutionService
{
    private readonly HookRegistry _hookRegistry;
    private readonly Dictionary<HookType, IHookExecutor> _executors;
    private readonly HookPolicyService _policyService;
    private readonly ILogger<HookExecutionService> _logger;

    public HookExecutionService(
        HookRegistry hookRegistry,
        [FromKeyedServices(HookType.Command)] IHookExecutor commandExecutor,
        [FromKeyedServices(HookType.Notification)] IHookExecutor notificationExecutor,
        [FromKeyedServices(HookType.Http)] IHookExecutor httpExecutor,
        HookPolicyService policyService,
        ILogger<HookExecutionService> logger)
    {
        _hookRegistry = hookRegistry ?? throw new ArgumentNullException(nameof(hookRegistry));
        _policyService = policyService ?? throw new ArgumentNullException(nameof(policyService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _executors = new Dictionary<HookType, IHookExecutor>
        {
            [HookType.Command] = commandExecutor,
            [HookType.Notification] = notificationExecutor,
            [HookType.Http] = httpExecutor,
        };
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

        var result = await ExecuteAndAggregateAsync(hooks, payload, ct).ConfigureAwait(false);

        RemoveOnceHooks(hooks);

        return result;
    }

    private void RemoveOnceHooks(List<HookRegistration> hooks)
    {
        foreach (var hook in hooks.Where(h => h.Once))
        {
            _hookRegistry.Unregister(hook.Name);
        }
    }

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
