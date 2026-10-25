namespace OneCode.App.Services.Agent;

/// <summary>
/// W4-D: single source for sub-goal LoopAgent MaxIterations (and budget headroom checks).
/// public：TodoStripView（OneCode.Tui）渲染子目标轮次时跨程序集消费同一常量。
/// </summary>
public static class GoalLoopDefaults
{
    /// <summary>Maps 1:1 to <c>LoopAgentOptions.MaxIterations</c> — no second attempt counter.</summary>
    public const int MaxAttemptsPerSubGoal = 3;
}
