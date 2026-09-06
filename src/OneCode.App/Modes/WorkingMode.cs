namespace OneCode.App.Modes;

/// <summary>
/// Four working modes supported by the app.
///
/// - <see cref="Build"/>: Agent directly analyzes and executes the request.
/// - <see cref="Plan"/>:  Agent first produces a plan card; user approves before execution.
/// - <see cref="Team"/>:  Multi-agent team coordination (Magentic or GroupChat strategy).
/// - <see cref="Goal"/>:  Autonomous goal-driven loop with AI evaluation.
/// </summary>
public enum WorkingMode
{
    /// <summary>BUILD — direct execution.</summary>
    Build = 0,

    /// <summary>PLAN — plan-then-execute flow.</summary>
    Plan = 1,

    /// <summary>TEAM — multi-agent team mode.</summary>
    Team = 2,

    /// <summary>GOAL — autonomous goal-driven loop with AI evaluation.</summary>
    Goal = 3,
}

/// <summary>
/// Orchestration strategies for the TEAM mode (driven by team.yaml).
///
/// 编排模式（Magentic / GroupChat / ParallelDag）是团队定义的固定属性，
/// 由 team.yaml 的 template 字段声明，运行期不可覆盖——用户只能通过
/// 切换团队来选择不同的执行方式。
/// </summary>
public sealed class WorkingModeController
{
    /// <summary>Currently active working mode.</summary>
    public WorkingMode Mode
    {
        get => field;
        set
        {
            if (field == value) return;
            var previous = field;
            field = value;
            ModeChanged?.Invoke(this, new WorkingModeChangedEventArgs(previous, field));
        }
    }

    /// <summary>Raised whenever <see cref="Mode"/> changes.</summary>
    public event EventHandler<WorkingModeChangedEventArgs>? ModeChanged;

    public WorkingModeController(WorkingMode initialMode = WorkingMode.Build)
    {
        Mode = initialMode;
    }

    /// <summary>
    /// Cycles the mode BUILD → PLAN → TEAM → GOAL → BUILD.
    /// </summary>
    public WorkingMode CycleMode()
    {
        Mode = (WorkingMode)(((int)Mode + 1) % 4);
        return Mode;
    }

    /// <summary>Uppercase short tag displayed by <see cref="OneCode.App.Tui.AgentStatusBar"/>.</summary>
    public string ModeTag => Mode switch
    {
        WorkingMode.Build => "BUILD",
        WorkingMode.Plan => "PLAN",
        WorkingMode.Team => "TEAM",
        WorkingMode.Goal => "GOAL",
        _ => "???",
    };
}

/// <summary>Event payload for <see cref="WorkingModeController.ModeChanged"/>.</summary>
public sealed class WorkingModeChangedEventArgs(
    WorkingMode previous,
    WorkingMode current) : EventArgs
{
    public WorkingMode PreviousMode { get; } = previous;
    public WorkingMode CurrentMode { get; } = current;
}
