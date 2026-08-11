namespace OneCode.Core.Hooks.Notifications;

/// <summary>
/// 通知渠道 Provider 接口——每种外部消息系统（飞书/企业微信/钉钉等）实现此接口。
///
/// 与 IHookExecutor 的设计一致：通过 IEnumerable 注入，按 Name 字典查找。
/// 新增渠道只需实现此接口 + DI 注册，无需修改任何现有代码。
/// </summary>
public interface INotificationProvider
{
    /// <summary>Provider 唯一名称（如 "feishu" / "wechat_work"），与 HookConfig.Provider 匹配</summary>
    string Name { get; }

    /// <summary>
    /// 发送通知消息
    /// </summary>
    /// <returns>发送结果</returns>
    Task<NotificationSendResult> SendAsync(
        NotificationMessage message,
        string webhookUrl,
        string? secret,
        CancellationToken ct);
}

/// <summary>
/// 通知消息 DTO——跨渠道统一的消息表示
/// </summary>
public sealed record NotificationMessage
{
    /// <summary>消息正文（纯文本，Provider 负责转换为渠道特定格式）</summary>
    public required string Text { get; init; }

    /// <summary>消息标题（可选，部分渠道支持）</summary>
    public string? Title { get; init; }

    /// <summary>触发事件名称（用于上下文信息）</summary>
    public string? Event { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 通知发送结果
/// </summary>
public sealed record NotificationSendResult
{
    public bool Success { get; init; }

    public string? ErrorMessage { get; init; }

    public int? StatusCode { get; init; }

    public static NotificationSendResult Ok() => new() { Success = true };

    public static NotificationSendResult Fail(string error, int? statusCode = null) => new()
    {
        Success = false,
        ErrorMessage = error,
        StatusCode = statusCode,
    };
}
