namespace OneCode.Core.Hooks;

/// <summary>
/// Hook 结果聚合器——将同一拦截点的全部匹配 HookResult 合并为单个 AggregatedHookResult。
///
/// 合并策略（产品语义：全部匹配项都执行，再聚合）：
/// - 裁决字段：任一 HookResult 带 BlockingError 即计入 BlockingErrors（保序）
/// - 附加上下文：累加
/// - 字符串：last-write-wins
/// </summary>
public static class HookResultAggregator
{
    /// <summary>
    /// 聚合多个 HookResult 为单个 AggregatedHookResult
    /// </summary>
    /// <param name="results">要聚合的结果序列</param>
    /// <returns>聚合后的结果</returns>
    public static AggregatedHookResult Aggregate(IEnumerable<HookResult?> results)
    {
        List<HookBlockingError> blockingErrors = [];
        List<string> additionalContexts = [];
        string? message = null;

        foreach (var result in results)
        {
            if (result is null) continue;

            if (result.Message is not null) message = result.Message;
            else if (result.SystemMessage is not null) message = result.SystemMessage;
            if (result.BlockingError is not null) blockingErrors.Add(result.BlockingError);
            if (result.AdditionalContext is not null) additionalContexts.Add(result.AdditionalContext);
        }

        return new AggregatedHookResult
        {
            Message = message,
            BlockingErrors = blockingErrors.Count > 0 ? blockingErrors : null,
            AdditionalContexts = additionalContexts.Count > 0 ? additionalContexts : null,
        };
    }
}
