namespace OneCode.Core.Goals;

/// <summary>
/// GOAL 模式预算模型（三级预算 + 100% 强制终止策略）。
///
/// 设计目的：
/// 防止 LLM 失控循环、成本失控、上下文窗口溢出三类风险。
/// 单一 attempt 计数无法覆盖所有维度，因此引入 token / 时间 / 美元 三个维度。
///
/// 触发策略：
/// - 70% 预算消耗：TUI 显示黄色警告，继续执行
/// - 90% 预算消耗：TUI 显示橙色警告，继续执行
/// - 100% 预算消耗：强制终止，保存 Checkpoint，输出汇总报告
///
/// null 字段表示该维度不限制（用户可按需关闭某维度）。
/// </summary>
public sealed record GoalBudget
{
    /// <summary>
    /// 跨所有子目标的"总 attempt 次数"上限。
    /// 每个 attempt = 一次 MainAgentRunner.RunStreamingAsync 调用。
    /// 默认 20（配合 MaxAttemptsPerSubGoal=3，可完成约 6-7 个子目标）。
    /// </summary>
    public int MaxSubGoalAttempts { get; init; } = 20;

    /// <summary>
    /// 累计输入 + 输出 token 总量上限。null 表示不限制。
    /// 默认 200k（约 Claude Sonnet 单次会话合理上限）。
    /// </summary>
    public long? MaxTotalTokens { get; init; } = 200_000;

    /// <summary>
    /// 墙钟时间上限。null 表示不限制。
    /// 默认 2 小时（防止用户启动后忘记，烧掉整晚 API 费用）。
    /// </summary>
    public TimeSpan? MaxWallClock { get; init; } = TimeSpan.FromHours(2);

    /// <summary>
    /// 累计美元成本上限。null 表示不限制。
    /// 默认 5.0 USD（适合中等粒度任务；系统级目标用户应显式调高）。
    /// </summary>
    public decimal? MaxCostUsd { get; init; } = 5.0m;

    /// <summary>
    /// 计算当前预算消耗比例（0.0 ~ 1.0）。
    /// 取所有已启用维度的最大消耗比例，确保任一维度触达阈值即触发对应策略。
    /// </summary>
    public double ComputeConsumptionRatio(GoalBudgetUsage usage)
    {
        var ratios = new List<double>();

        if (MaxSubGoalAttempts > 0)
            ratios.Add((double)usage.TotalAttempts / MaxSubGoalAttempts);

        if (MaxTotalTokens is { } tokenLimit)
            ratios.Add((double)usage.TotalTokens / tokenLimit);

        if (MaxWallClock is { } timeLimit && usage.Elapsed > TimeSpan.Zero)
            ratios.Add(usage.Elapsed.Value.TotalSeconds / timeLimit.TotalSeconds);

        if (MaxCostUsd is { } costLimit)
            ratios.Add((double)usage.EstimatedCostUsd / (double)costLimit);

        return ratios.Count == 0 ? 0.0 : ratios.Max();
    }

    /// <summary>
    /// 判断是否应触发强制终止（100% 消耗）。
    /// </summary>
    public bool ShouldForceTerminate(GoalBudgetUsage usage)
        => ComputeConsumptionRatio(usage) >= 1.0;

    /// <summary>
    /// 判断是否应触发警告（70% 或 90%）。
    /// 返回 null 表示无警告；返回 WarningLevel.Early (70%) 或 WarningLevel.Late (90%)。
    /// </summary>
    public GoalBudgetWarningLevel? EvaluateWarning(GoalBudgetUsage usage)
    {
        var ratio = ComputeConsumptionRatio(usage);
        if (ratio >= 0.9) return GoalBudgetWarningLevel.Late;
        if (ratio >= 0.7) return GoalBudgetWarningLevel.Early;
        return null;
    }
}

/// <summary>
/// GOAL 模式预算消耗快照（用于实时计算消耗比例）。
/// </summary>
public sealed record GoalBudgetUsage(
    int TotalAttempts,
    long TotalTokens,
    TimeSpan? Elapsed,
    decimal EstimatedCostUsd);

/// <summary>
/// 预算警告级别。
/// </summary>
public enum GoalBudgetWarningLevel
{
    /// <summary>70% 消耗，黄色警告。</summary>
    Early,

    /// <summary>90% 消耗，橙色警告。</summary>
    Late,
}
