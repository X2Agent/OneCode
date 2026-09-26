using OneCode.App.Services.Notifier;
using OneCode.Core.Config;

namespace OneCode.App.Query;

/// <summary>Hook 触发与桌面通知的封装，拆自 <see cref="QueryStreamEngine"/>。</summary>
internal sealed class HookDispatcher(
    IHookExecutionService hookExecutionService,
    IConfigManager configManager,
    INotifierService notifierService)
{
    private readonly IHookExecutionService _hookExecutionService = hookExecutionService;
    private readonly IConfigManager _configManager = configManager;
    private readonly INotifierService _notifierService = notifierService;

    /// <summary>
    /// 触发拦截点并返回聚合结果。matcher 的实际比较值由调用方通过
    /// <paramref name="actualMatcherValue"/> 显式传入（不从 payload 字段猜测，
    /// 语义声明见 <see cref="HookPointMetadataRegistry"/>）；<paramref name="configure"/> 用于
    /// 填充拦截点专属 payload 字段（UserMessage / ToolName / OutputText 等）。
    /// </summary>
    public async Task<AggregatedHookResult?> FireHookAsync(
        HookInterceptionPoint point,
        SessionId? sessionId,
        string? workingDirectory,
        CancellationToken ct = default,
        string? actualMatcherValue = null,
        Action<HookPayload>? configure = null)
    {
        var payload = new HookPayload
        {
            Point = point,
            SessionId = sessionId?.ToString(),
            Cwd = workingDirectory ?? Environment.CurrentDirectory,
        };

        configure?.Invoke(payload);

        return await _hookExecutionService.FireAsync(payload, actualMatcherValue, ct).ConfigureAwait(false);
    }

    public async Task NotifyAsync(string title, string message, CancellationToken ct)
    {
        // 配置开关：默认关闭，用户需显式开启 notificationsEnabled=true 才发桌面通知
        if (!_configManager.Current.Effective.NotificationsEnabled) return;
        if (!_notifierService.IsSupported) return;
        await _notifierService.SendNotificationAsync(title, message, ct).ConfigureAwait(false);
    }
}
