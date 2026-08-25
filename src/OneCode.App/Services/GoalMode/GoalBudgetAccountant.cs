using OneCode.Core.Goals;

namespace OneCode.App.Services.GoalMode;

/// <summary>
/// GOAL 运行预算的纯会计函数集：墙钟滚动、用量快照构建、证据差值累计。
/// 全部为 record-in / record-out 的静态函数，无 I/O、无实例状态——
/// 预算公式的回归（负扣减保护、墙钟区间语义、旧快照兼容）由此处的单元测试锚定。
/// </summary>
internal static class GoalBudgetAccountant
{
    /// <summary>
    /// Fix-7：墙钟语义 = 仅累计运行区间。LastActivityAt 是上次活动时间戳，
    /// Paused / 进程离线期间的时间不计入；首次调用只打点不累计。
    /// </summary>
    public static GoalBudgetSnapshot RollForwardWallClock(GoalBudgetSnapshot budget)
    {
        var now = DateTimeOffset.UtcNow;
        if (budget.LastActivityAt is not { } last)
            return budget with { LastActivityAt = now };
        return budget with
        {
            AccumulatedElapsed = budget.AccumulatedElapsed + (now - last),
            LastActivityAt = now,
        };
    }

    public static GoalBudgetUsage BuildUsage(GoalBudgetSnapshot budget)
        => new(
            budget.TotalAttempts,
            budget.TotalInputTokens + budget.TotalOutputTokens,
            ResolveElapsed(budget),
            budget.EstimatedCostUsd);

    /// <summary>旧版本快照兼容：无累加墙钟时回退到“自 StartedAt 起的总墙钟”。</summary>
    public static TimeSpan? ResolveElapsed(GoalBudgetSnapshot budget)
        => budget.LastActivityAt is null && budget.AccumulatedElapsed == TimeSpan.Zero
            ? DateTimeOffset.UtcNow - budget.StartedAt
            : budget.AccumulatedElapsed;

    /// <summary>
    /// Fix-2/F-02：把步骤证据计入预算。差值公式加下限保护——budget-skip 等场景下
    /// 新证据计数为 0 时，不得对旧证据做负扣减导致预算消耗回退。
    /// </summary>
    public static GoalBudgetSnapshot AccumulateEvidence(
        GoalBudgetSnapshot budget,
        GoalStepExecutionEvidence? previous,
        GoalStepExecutionEvidence evidence)
        => budget with
        {
            TotalAttempts = budget.TotalAttempts + Math.Max(0, evidence.Attempts - (previous?.Attempts ?? 0)),
            TotalInputTokens = budget.TotalInputTokens + Math.Max(0, evidence.InputTokens - (previous?.InputTokens ?? 0)),
            TotalOutputTokens = budget.TotalOutputTokens + Math.Max(0, evidence.OutputTokens - (previous?.OutputTokens ?? 0)),
        };

    /// <summary>子目标递归分解消耗的 LLM 用量直接累加（分解本身不产生步骤证据）。</summary>
    public static GoalBudgetSnapshot AddLlmUsage(GoalBudgetSnapshot budget, long inputTokens, long outputTokens)
        => budget with
        {
            TotalInputTokens = budget.TotalInputTokens + inputTokens,
            TotalOutputTokens = budget.TotalOutputTokens + outputTokens,
        };
}
