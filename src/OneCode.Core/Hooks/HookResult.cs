namespace OneCode.Core.Hooks;

/// <summary>
/// 钩子执行结果
/// </summary>
public sealed record HookResult
{
    public string? Message { get; init; }
    public string? SystemMessage { get; init; }
    public HookBlockingError? BlockingError { get; init; }
    public HookOutcome Outcome { get; init; } = HookOutcome.Success;
    public bool PreventContinuation { get; init; }
    public string? AdditionalContext { get; init; }
    public Dictionary<string, object>? UpdatedInput { get; init; }
}

/// <summary>
/// 聚合的钩子执行结果（多个钩子合并后）
/// </summary>
public sealed record AggregatedHookResult
{
    public string? Message { get; init; }
    public IReadOnlyList<HookBlockingError>? BlockingErrors { get; init; }
    public bool PreventContinuation { get; init; }
    public IReadOnlyList<string>? AdditionalContexts { get; init; }
    public Dictionary<string, object>? UpdatedInput { get; init; }
}

public enum HookOutcome
{
    Success,
    Blocking,
    NonBlockingError,
    Cancelled,
}

/// <summary>
/// 钩子阻断错误信息
/// </summary>
public sealed record HookBlockingError(
    string Error,
    string Command);
