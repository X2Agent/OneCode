using OneCode.Core.Hooks.Notifications;

namespace OneCode.App.Services.Hooks;

/// <summary>
/// Notification 类型 Hook 执行器——通过 INotificationProvider 分发到外部消息系统。
///
/// 模板插值：支持 {{Field}} 语法替换 HookPayload 字段（如 {{Event}} / {{UserMessage}}），
/// 由 <see cref="HookTemplateRenderer"/> 统一实现。
/// Provider 解析：通过 IEnumerable 注入，按 Name 字典查找（与 IHookExecutor 模式一致）。
/// </summary>
public sealed class NotificationHookExecutor : IHookExecutor
{
    private readonly Dictionary<string, INotificationProvider> _providers;
    private readonly ILogger<NotificationHookExecutor> _logger;

    public NotificationHookExecutor(
        IEnumerable<INotificationProvider> providers,
        ILogger<NotificationHookExecutor> logger)
    {
        _providers = providers?.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, INotificationProvider>(StringComparer.OrdinalIgnoreCase);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public HookType Type => HookType.Notification;

    public async Task<HookResult?> ExecuteAsync(
        HookPayload payload, HookConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.Provider))
        {
            _logger.LogWarning("Notification hook has no provider specified");
            return new HookResult
            {
                Outcome = HookOutcome.NonBlockingError,
                Message = "Notification hook missing 'provider' field",
            };
        }

        if (!_providers.TryGetValue(config.Provider, out var provider))
        {
            _logger.LogWarning("Notification provider '{Provider}' not registered. Available: {Available}",
                config.Provider, string.Join(", ", _providers.Keys));
            return new HookResult
            {
                Outcome = HookOutcome.NonBlockingError,
                Message = $"Unknown notification provider: {config.Provider}",
            };
        }

        if (string.IsNullOrWhiteSpace(config.WebhookUrl))
        {
            _logger.LogWarning("Notification hook has no webhookUrl specified");
            return new HookResult
            {
                Outcome = HookOutcome.NonBlockingError,
                Message = "Notification hook missing 'webhookUrl' field",
            };
        }

        var messageText = HookTemplateRenderer.Render(config.Message ?? string.Empty, payload);
        var message = new NotificationMessage
        {
            Text = messageText,
            Title = config.StatusMessage,
            Event = payload.Event.ToString(),
            Timestamp = payload.Timestamp,
        };

        var timeoutMs = config.TimeoutMs ?? 5000;
        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var result = await provider.SendAsync(message, config.WebhookUrl, config.Secret, linkedCts.Token)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                return new HookResult
                {
                    Outcome = HookOutcome.NonBlockingError,
                    Message = $"Notification failed: {result.ErrorMessage}",
                };
            }

            return null; // 成功时返回 null（与 HttpHookExecutor 一致）
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning("Notification provider '{Provider}' timed out after {TimeoutMs}ms",
                config.Provider, timeoutMs);
            return new HookResult
            {
                Outcome = HookOutcome.NonBlockingError,
                Message = $"Notification timed out after {timeoutMs}ms",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification provider '{Provider}' threw exception", config.Provider);
            return new HookResult
            {
                Outcome = HookOutcome.NonBlockingError,
                Message = $"Notification error: {ex.Message}",
            };
        }
    }
}
