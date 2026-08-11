namespace OneCode.Core.Hooks;

/// <summary>
/// Hook 结果聚合器——将多个 HookResult 合并为单个 AggregatedHookResult
///
/// 合并策略：
/// - 布尔字段：OR（任一为 true 则结果为 true）
/// - 列表字段：累加
/// - 字符串字段：last-write-wins
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
        string? systemMessage = null;
        bool preventContinuation = false;
        Dictionary<string, object>? updatedInput = null;

        foreach (var result in results)
        {
            if (result is null) continue;

            if (result.Message is not null) message = result.Message;
            if (result.SystemMessage is not null) systemMessage = result.SystemMessage;
            if (result.BlockingError is not null) blockingErrors.Add(result.BlockingError);
            if (result.PreventContinuation)
                preventContinuation = true;
            if (result.AdditionalContext is not null) additionalContexts.Add(result.AdditionalContext);
            if (result.UpdatedInput is not null) updatedInput = result.UpdatedInput;
        }

        return new AggregatedHookResult
        {
            Message = message ?? systemMessage,
            BlockingErrors = blockingErrors.Count > 0 ? blockingErrors : null,
            PreventContinuation = preventContinuation,
            AdditionalContexts = additionalContexts.Count > 0 ? additionalContexts : null,
            UpdatedInput = updatedInput,
        };
    }
}
