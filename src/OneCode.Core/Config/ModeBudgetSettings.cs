using CoreConstants = OneCode.Core.Constants;

using OneCode.Core.Goals;

namespace OneCode.Core.Config;

/// <summary>
/// 各模式预算的统一配置视图（控制面并入）。
/// Goal 全开（attempt / token / 墙钟，三级警告）；Team/Build 仅 <c>maxTurns</c>（Team 的
/// team.yaml 每团队配置优先）；Plan 无模式预算（token 预算由 BudgetGuardRunMiddleware
/// 全模式统一生效，与模式预算正交）。
/// </summary>
public sealed record ModeBudgetSettings
{
    /// <summary>Build/Team 的全局 turns 上限（<c>maxTurns</c>，默认 100；Team 的 team.yaml 每团队配置优先）。</summary>
    public int MaxTurns { get; init; } = CoreConstants.Session.MaxTurnsDefault;

    /// <summary>跨所有子目标的总 attempt 上限（<c>goal.maxSubGoalAttempts</c>，默认 20）。</summary>
    public int MaxSubGoalAttempts { get; init; } = 20;

    /// <summary>单子目标轮数上限（<c>goal.maxTurnsPerSubGoal</c>，默认 50）。</summary>
    public int MaxTurnsPerSubGoal { get; init; } = 50;

    /// <summary>累计 token 上限（<c>goal.maxTotalTokens</c>，默认 200000；≤0 表示不限制）。</summary>
    public long MaxTotalTokens { get; init; } = 200_000;

    /// <summary>墙钟上限小时数（<c>goal.maxWallClockHours</c>，默认 2.0；≤0 表示不限制）。</summary>
    public double MaxWallClockHours { get; init; } = 2.0;

    /// <summary>
    /// <c>/loop</c> 运行时循环调用 inner agent 的硬上限（<c>loop.maxIterations</c>，默认 3）。
    /// 对应 <c>LoopAgentOptions.MaxIterations</c>，评估器无法突破。
    /// 配置值被夹在 1..<see cref="MaxLoopIterationsUpperBound"/>。
    /// </summary>
    public int MaxLoopIterations { get; init; } = 3;

    /// <summary>
    /// <c>loop.maxIterations</c> 与 <c>/loop --max</c> 的硬上界，取 GOAL 单子目标重试上限（20）。
    /// 二者语义相同（"同一目标重试几次"），而每轮都是一次完整自主 agent run（单轮默认上限 100 次
    /// 工具调用）——无界放大会把一次误输入变成上万次无人值守执行。
    /// </summary>
    public const int MaxLoopIterationsUpperBound = 20;

    /// <summary>墙钟上限；未配置（≤0）表示不限制。</summary>
    public TimeSpan? MaxWallClock => MaxWallClockHours > 0 ? TimeSpan.FromHours(MaxWallClockHours) : null;

    /// <summary>token 上限；未配置（≤0）表示不限制。</summary>
    public long? TotalTokenLimit => MaxTotalTokens > 0 ? MaxTotalTokens : null;

    /// <summary>映射为 GOAL 三级预算模型（attempt / token / 墙钟三维度单源）。</summary>
    public GoalBudget ToGoalBudget() => new()
    {
        MaxSubGoalAttempts = MaxSubGoalAttempts,
        MaxTotalTokens = TotalTokenLimit,
        MaxWallClock = MaxWallClock,
    };

    /// <summary>从生效配置构建模式预算视图（键缺失时取内置默认值；模式差异由调用方的装配点决定）。</summary>
    public static ModeBudgetSettings FromSettings(AppSettings settings) => new()
    {
        MaxTurns = settings.MaxTurns,
        MaxSubGoalAttempts = settings.Get(CoreConstants.ConfigKeys.GoalMaxSubGoalAttempts, 20),
        MaxTurnsPerSubGoal = settings.Get(CoreConstants.ConfigKeys.GoalMaxTurnsPerSubGoal, 50),
        MaxTotalTokens = settings.Get(CoreConstants.ConfigKeys.GoalMaxTotalTokens, 200_000L),
        MaxWallClockHours = settings.Get(CoreConstants.ConfigKeys.GoalMaxWallClockHours, 2.0),
        MaxLoopIterations = Math.Clamp(
            settings.Get(CoreConstants.ConfigKeys.LoopMaxIterations, 3),
            1,
            MaxLoopIterationsUpperBound),
    };
}
