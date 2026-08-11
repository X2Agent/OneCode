namespace OneCode.App.Services.Agent;

/// <summary>
/// Ambient identity for the currently executing main-agent run.
/// Tool methods do not receive the MAF run descriptor directly, so the chat boundary
/// publishes the immutable run ID through AsyncLocal for the duration of the run.
/// </summary>
public static class OneCodeAgentRunContext
{
    private static readonly AsyncLocal<string?> CurrentRunIdSlot = new();
    private static readonly AsyncLocal<string?> CurrentBuildRunIdSlot = new();

    public static string? CurrentRunId
    {
        get => CurrentRunIdSlot.Value;
        set => CurrentRunIdSlot.Value = value;
    }

    /// <summary>Current persisted BuildRun identity for task isolation and evidence linkage.</summary>
    public static string? CurrentBuildRunId
    {
        get => CurrentBuildRunIdSlot.Value;
        set => CurrentBuildRunIdSlot.Value = value;
    }
}
