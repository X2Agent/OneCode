using OneCode.App.Services.Notifier;
using OneCode.Core.Config;

namespace OneCode.App.Query;

/// <summary>Hook 触发与桌面通知的封装，拆自 <see cref="QueryStreamEngine"/>。</summary>
internal sealed class HookDispatcher
{
    private readonly IHookExecutionService _hookExecutionService;
    private readonly IConfigManager _configManager;
    private readonly INotifierService _notifierService;

    public HookDispatcher(
        IHookExecutionService hookExecutionService,
        IConfigManager configManager,
        INotifierService notifierService)
    {
        _hookExecutionService = hookExecutionService;
        _configManager = configManager;
        _notifierService = notifierService;
    }

    /// <summary>
    /// 触发 hook 事件并返回聚合结果。matcher 的实际比较值由调用方通过
    /// <paramref name="actualMatcherValue"/> 显式传入（不从 payload 字段猜测，
    /// 语义声明见 HookEventMetadataRegistry）；<paramref name="configure"/> 用于
    /// 填充事件专属 payload 字段（UserMessage / TerminalReason / ErrorCategory / ToolName 等）。
    /// </summary>
    public async Task<AggregatedHookResult?> FireHookAsync(
        HookEvent @event,
        SessionId? sessionId,
        string? workingDirectory,
        CancellationToken ct = default,
        string? actualMatcherValue = null,
        Action<HookPayload>? configure = null)
    {
        var payload = new HookPayload
        {
            Event = @event,
            SessionId = sessionId?.ToString(),
            Cwd = workingDirectory ?? Environment.CurrentDirectory,
        };

        configure?.Invoke(payload);

        return await _hookExecutionService.FireAsync(payload, actualMatcherValue, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Notification hook 触发器（matcher=permission_prompt）：在 TUI 审批请求下发前执行。
    /// 挂在 <see cref="OneCode.App.Services.Agent.MainAgentRunOptions.OnPermissionPrompt"/> 上，由 ApprovalBroker 调用；
    /// 审批流程不受 hook 结果影响（无阻断语义）。
    /// </summary>
    public Func<string, CancellationToken, Task> OnPermissionPromptHook(QueryStreamRequest request) =>
        (toolName, hookCt) => FireHookAsync(
            HookEvent.Notification,
            request.SessionId,
            request.WorkingDirectory,
            hookCt,
            actualMatcherValue: "permission_prompt",
            configure: p => p.ToolName = toolName);

    public async Task NotifyAsync(string title, string message, CancellationToken ct)
    {
        // 配置开关：默认关闭，用户需显式开启 notificationsEnabled=true 才发桌面通知
        if (!_configManager.Current.Effective.NotificationsEnabled) return;
        if (!_notifierService.IsSupported) return;
        await _notifierService.SendNotificationAsync(title, message, ct).ConfigureAwait(false);
    }
}
