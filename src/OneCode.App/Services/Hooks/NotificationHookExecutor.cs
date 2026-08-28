using OneCode.Core.Hooks.Notifications;

namespace OneCode.App.Services.Hooks;

/// <summary>
/// Notification 类型 Hook 执行器——把消息分发到外部消息系统。
///
/// 渠道解析经 <see cref="NotificationProviderRegistry"/>：声明式定义（notification-providers.json）
/// 优先，编译型 INotificationProvider 兜底，同名声明式胜出。
/// 模板插值：支持 {{Field}} 语法替换 HookPayload 字段（如 {{Event}} / {{UserMessage}}），
/// 由 <see cref="HookTemplateRenderer"/> 统一实现。
/// </summary>
public sealed class NotificationHookExecutor : IHookExecutor
{
    private readonly NotificationProviderRegistry _registry;
    private readonly ILogger<NotificationHookExecutor> _logger;

    public NotificationHookExecutor(
        NotificationProviderRegistry registry,
        ILogger<NotificationHookExecutor> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
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

        var provider = _registry.Resolve(config.Provider);
        if (provider is null)
        {
            _logger.LogWarning("Notification provider '{Provider}' not registered. Available: {Available}",
                config.Provider, string.Join(", ", _registry.Names));
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

        // C1：webhookUrl / secret 支持 ${ENV_VAR} 与 dpapi: 展开后才传给 Provider
        var webhookUrl = HookSecretExpander.Expand(config.WebhookUrl);
        var secret = HookSecretExpander.Expand(config.Secret);

        var timeoutMs = config.TimeoutMs ?? 5000;
        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var result = await provider.SendAsync(message, webhookUrl, secret, linkedCts.Token)
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
