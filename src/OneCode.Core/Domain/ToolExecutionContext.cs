namespace OneCode.Core.Domain;

/// <summary>
/// 工具执行结果的 guidance 类型。
/// </summary>
/// <remarks>
/// 用于 StateMachineMiddleware 区分"真正的工具失败"和"恢复指导消息"：
/// guidance 不递增 <c>ConsecutiveFailures</c>，避免双重计数。
/// </remarks>
public enum GuidanceKind
{
    /// <summary>非 guidance：普通工具结果（成功或失败）。</summary>
    None,

    /// <summary>ToolResultUnwrapMiddleware 的恢复指导（[RECOVERY]，如 overloaded/529）。</summary>
    Recovery,

    /// <summary>StateMachineMiddleware 的任务级恢复指导（[STATE MACHINE]）。</summary>
    TaskRecovery,
}

/// <summary>
/// 结构化工具执行上下文：在中间件管道中传递 IsError、Guidance、IsVerificationFailure 语义。
/// </summary>
/// <remarks>
/// 存在原因：<c>ToolResult</c> 序列化为 string 后 <c>IsError</c> 语义丢失，
/// 用强类型字段准确传递，不依赖字符串前缀匹配。
/// 通过 MAF <c>AgentSession.StateBag</c> 共享（per-session，MAF 串行执行等效 per-call）。
/// 每次工具调用在 <c>ToolResultUnwrapMiddleware</c> 的 pre 部分重置。
/// </remarks>
public sealed class ToolExecutionContext
{
    /// <summary>
    /// 工具执行是否为错误。由 ToolResultUnwrapMiddleware 写入；可恢复错误（overloaded/529）覆盖为 false。
    /// </summary>
    public bool IsError { get; set; }

    /// <summary>
    /// guidance 类型。非 None 时视为 guidance 消息，不递增 ConsecutiveFailures。
    /// </summary>
    public GuidanceKind Guidance { get; set; } = GuidanceKind.None;

    /// <summary>
    /// 是否为验证失败（编译/类型检查错误）。触发立即 Active→Recovering 转移。
    /// </summary>
    public bool IsVerificationFailure { get; set; }
}
