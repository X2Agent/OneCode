namespace OneCode.Core.Hooks;

/// <summary>
/// 单个钩子的执行结果。
/// </summary>
/// <remarks>
/// <see cref="BlockingError"/> 非空即构成 deny 裁决；deny 的稳定原因取 <see cref="HookBlockingError.Error"/>。
/// </remarks>
public sealed record HookResult
{
    public string? Message { get; init; }
    public string? SystemMessage { get; init; }
    public HookBlockingError? BlockingError { get; init; }
    public HookOutcome Outcome { get; init; } = HookOutcome.Success;

    /// <summary>注入的附加上下文（input 节点并入本轮输入）。</summary>
    public string? AdditionalContext { get; init; }
}

/// <summary>
/// 同一拦截点全部匹配钩子执行完后的聚合结果。
/// </summary>
public sealed record AggregatedHookResult
{
    public string? Message { get; init; }
    public IReadOnlyList<HookBlockingError>? BlockingErrors { get; init; }
    public IReadOnlyList<string>? AdditionalContexts { get; init; }
}

public enum HookOutcome
{
    Success,
    Blocking,
    NonBlockingError,
    Cancelled,
}

/// <summary>
/// 阻断（deny）裁决的稳定原因与来源命令。
/// </summary>
public sealed record HookBlockingError(
    string Error,
    string Command);
