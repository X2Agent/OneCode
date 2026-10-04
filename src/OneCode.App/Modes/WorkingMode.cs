namespace OneCode.App.Modes;

/// <summary>
/// Working modes supported by the app.
///
/// - <see cref="Build"/>: Agent directly analyzes and executes the request.
/// - <see cref="Plan"/>:  Agent first produces a plan card; user approves before execution.
/// - <see cref="Goal"/>:  Autonomous goal-driven loop with AI evaluation.
/// </summary>
public enum WorkingMode
{
    /// <summary>BUILD — direct execution.</summary>
    Build = 0,

    /// <summary>PLAN — plan-then-execute flow.</summary>
    Plan = 1,

    /// <summary>GOAL — autonomous goal-driven loop with AI evaluation.</summary>
    Goal = 2,
}

/// <summary>
/// Working-mode controller: owns the active mode, its change notification and the
/// Tab cycle order. 模式标签统一由本类提供（单一事实源），TUI 层不再手写字符串映射。
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
    /// Cycles the mode BUILD → PLAN → GOAL → BUILD.
    /// </summary>
    public WorkingMode CycleMode()
    {
        Mode = (WorkingMode)(((int)Mode + 1) % 3);
        return Mode;
    }

    /// <summary>Uppercase short tag displayed by <see cref="OneCode.App.Tui.AgentStatusBar"/>.</summary>
    public string ModeTag => Mode switch
    {
        WorkingMode.Build => "BUILD",
        WorkingMode.Plan => "PLAN",
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
