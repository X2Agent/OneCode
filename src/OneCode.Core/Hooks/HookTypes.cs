namespace OneCode.Core.Hooks;

/// <summary>
/// 钩子类型——3 种真实使用的执行器
/// </summary>
public enum HookType
{
    /// <summary>通过 CliWrap 执行外部进程，stdin 传 JSON payload，exit code 2 可阻断</summary>
    Command,

    /// <summary>
    /// 通知动作：通过 Provider 策略分发到飞书/企业微信等外部消息系统。
    /// 新增通知渠道只需实现 INotificationProvider 并注册到 DI，无需修改核心代码。
    /// </summary>
    Notification,

    /// <summary>
    /// 通用 HTTP 调用：通过 IHttpClientFactory 发起 GET/POST/PUT/DELETE 请求，
    /// 用于 webhook 通知、CI/CD 触发、自定义服务集成、审计回调等场景。
    /// 与 Notification 的区别：HTTP 面向通用 HTTP 调用（自定义 URL/Method/Headers/Body），
    /// Notification 面向消息推送业务场景（飞书/企微等固定渠道格式）。
    /// </summary>
    Http,
}
