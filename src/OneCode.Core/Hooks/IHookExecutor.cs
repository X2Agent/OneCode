namespace OneCode.Core.Hooks;

/// <summary>
/// 钩子执行器接口——按 HookType 分发的执行器
///
/// 每种 HookType 对应一个 IHookExecutor 实现：
/// - Command → CommandHookExecutor（CliWrap 外部进程）
/// - Notification → NotificationHookExecutor（飞书/企业微信等消息渠道）
/// - Http → HttpHookExecutor（通用 HTTP 调用，自定义 URL/Method/Headers/Body）
///
/// 新增执行器类型：实现 IHookExecutor + DI 注册为 IHookExecutor 即可，
/// HookExecutionService 会通过 Type 属性自动分发。
/// </summary>
public interface IHookExecutor
{
    HookType Type { get; }

    Task<HookResult?> ExecuteAsync(
        HookPayload payload,
        HookConfig config,
        CancellationToken ct);
}
