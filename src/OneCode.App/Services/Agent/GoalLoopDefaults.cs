namespace OneCode.App.Services.Agent;

/// <summary>
/// W4-D: single source for sub-goal LoopAgent MaxIterations (and budget headroom checks).
/// </summary>
internal static class GoalLoopDefaults
{
    /// <summary>Maps 1:1 to <c>LoopAgentOptions.MaxIterations</c> — no second attempt counter.</summary>
    public const int MaxAttemptsPerSubGoal = 3;
}
