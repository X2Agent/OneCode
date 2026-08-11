namespace OneCode.App.Tui;

/// <summary>
/// Four working modes supported by the TUI.
///
/// Design-spec §1.1:
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
/// Design-spec §1.3:
/// - <see cref="Magentic"/>:   Orchestrator-led, agents report back.
/// - <see cref="GroupChat"/>: Peer-to-peer round-robin discussion.
/// </summary>
public enum TeamStrategy
{
    /// <summary>Config — use the selected team's YAML template without a runtime override.</summary>
    Config = 0,

    /// <summary>Magentic — orchestrator-led workflow.</summary>
    Magentic = 1,

    /// <summary>GroupChat — peer round-robin discussion.</summary>
    GroupChat = 2,
}

/// <summary>
/// Centralised, observable state machine for the TUI working mode.
///
/// Threading: mutated only on the Terminal.Gui main loop. Listeners are
/// invoked synchronously from the setter methods.
/// </summary>
public sealed class WorkingModeController
{
    // Strategy retains explicit backing field because Mode setter resets it directly.
    private TeamStrategy _strategy;

    /// <summary>Currently active working mode.</summary>
    public WorkingMode Mode
    {
        get => field;
        set
        {
            if (field == value) return;
            var previous = field;
            field = value;
            // When leaving TEAM, reset to Config so the next Team run follows YAML by default.
            if (previous == WorkingMode.Team && value != WorkingMode.Team)
                _strategy = TeamStrategy.Config;
            ModeChanged?.Invoke(this, new WorkingModeChangedEventArgs(previous, field, _strategy));
        }
    }

    /// <summary>
    /// Currently active team strategy. Only meaningful when
    /// <see cref="Mode"/> is <see cref="WorkingMode.Team"/>; the setter is a
    /// no-op when called outside TEAM mode.
    /// </summary>
    public TeamStrategy Strategy
    {
        get => _strategy;
        set
        {
            if (Mode != WorkingMode.Team) return;
            if (_strategy == value) return;
            _strategy = value;
            ModeChanged?.Invoke(this, new WorkingModeChangedEventArgs(Mode, Mode, _strategy));
        }
    }

    /// <summary>Raised whenever <see cref="Mode"/> or <see cref="Strategy"/> changes.</summary>
    public event EventHandler<WorkingModeChangedEventArgs>? ModeChanged;

    public WorkingModeController(WorkingMode initialMode = WorkingMode.Build,
        TeamStrategy initialStrategy = TeamStrategy.Config)
    {
        Mode = initialMode;
        _strategy = initialStrategy;
    }

    /// <summary>
    /// Cycles the mode BUILD → PLAN → TEAM → GOAL → BUILD.
    /// </summary>
    public WorkingMode CycleMode()
    {
        Mode = (WorkingMode)(((int)Mode + 1) % 4);
        return Mode;
    }

    /// <summary>
    /// Within TEAM mode, toggles between Magentic and GroupChat.
    /// Outside TEAM mode, this is a no-op.
    /// </summary>
    public TeamStrategy ToggleStrategy()
    {
        if (Mode != WorkingMode.Team) return _strategy;
        Strategy = _strategy switch
        {
            TeamStrategy.Config => TeamStrategy.Magentic,
            TeamStrategy.Magentic => TeamStrategy.GroupChat,
            _ => TeamStrategy.Config,
        };
        return _strategy;
    }

    /// <summary>Shortcut: is TEAM mode active and the strategy is Magentic.</summary>
    public bool IsMagentic => Mode == WorkingMode.Team && _strategy == TeamStrategy.Magentic;

    /// <summary>Shortcut: is TEAM mode active and the strategy is GroupChat.</summary>
    public bool IsGroupChat => Mode == WorkingMode.Team && _strategy == TeamStrategy.GroupChat;

    /// <summary>Compact one-line label of the current mode (e.g. "BUILD", "TEAM · Magentic").</summary>
    public string ModeLabel => Mode switch
    {
        WorkingMode.Build => "BUILD",
        WorkingMode.Plan => "PLAN",
        WorkingMode.Team => $"TEAM · {_strategy switch
        {
            TeamStrategy.Config => "Config",
            TeamStrategy.Magentic => "Magentic",
            _ => "GroupChat",
        }}",
        WorkingMode.Goal => "GOAL",
        _ => "UNKNOWN",
    };

    /// <summary>Uppercase short tag displayed by <see cref="AgentStatusBar"/>.</summary>
    public string ModeTag => Mode switch
    {
        WorkingMode.Build => "BUILD",
        WorkingMode.Plan => "PLAN",
        WorkingMode.Team => "TEAM",
        WorkingMode.Goal => "GOAL",
        _ => "???",
    };

    /// <summary>True when the strategy tag should be visible in the status bar (only TEAM).</summary>
    public bool ShowStrategyTag => Mode == WorkingMode.Team;
}

/// <summary>Event payload for <see cref="WorkingModeController.ModeChanged"/>.</summary>
public sealed class WorkingModeChangedEventArgs(
    WorkingMode previous,
    WorkingMode current,
    TeamStrategy strategy) : EventArgs
{
    public WorkingMode PreviousMode { get; } = previous;
    public WorkingMode CurrentMode { get; } = current;
    public TeamStrategy CurrentStrategy { get; } = strategy;
}
