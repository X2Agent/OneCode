namespace OneCode.App.Query;

/// <summary>Mutable terminal-reason carrier rewritten by the durable BuildRun reload.</summary>
internal sealed class TerminalOutcomeState
{
    public required RunTerminalReason Reason { get; set; }

    public bool TransactionRolledBack { get; set; }

    public string? ValidationFailureSummary { get; set; }
}
