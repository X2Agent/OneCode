namespace OneCode.Core.Build;

/// <summary>
/// Represents the deterministic reason a Build run terminated.
/// Replaces the former <c>bool MaxTurnsReached</c> with a structured value
/// that the TUI can render distinctly.
/// </summary>
public enum BuildTerminalReason
{
    /// <summary>Agent completed normally and final validation passed (or no file changes).</summary>
    Completed,

    /// <summary>Turn limit was reached before the agent finished.</summary>
    TurnLimitReached,

    /// <summary>Budget was exhausted before the agent finished.</summary>
    BudgetExceeded,

    /// <summary>User cancelled the run (ESC or cancellation token).</summary>
    Cancelled,

    /// <summary>Final validation (build/test) failed; transaction was rolled back.</summary>
    ValidationFailed,

    /// <summary>Agent pipeline threw a non-cancellation exception.</summary>
    AgentException,

    /// <summary>A required permission was refused and could not be resolved.</summary>
    PermissionRefused,

    /// <summary>The run is waiting for required clarification or scope confirmation.</summary>
    ClarificationRequired,

    /// <summary>The run is blocked by an external dependency or workspace conflict.</summary>
    Blocked,
}
