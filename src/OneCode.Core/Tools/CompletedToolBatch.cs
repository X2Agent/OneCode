namespace OneCode.Core.Tools;

public enum ToolResultCompletion
{
    Succeeded,
    Rejected,
    Failed,
    Cancelled,
}

public sealed record CompletedToolCallRecord(
    string CallId,
    string ToolName,
    string ArgumentsJson,
    int Order);

public sealed record CompletedToolResultRecord(
    string CallId,
    string ToolName,
    string ResultJson,
    bool IsError,
    ToolResultCompletion Completion,
    int Order);

/// <summary>
/// A sealed tool-call batch. A batch is persistable only when every call has exactly one result.
/// </summary>
public sealed record CompletedToolBatch(
    string BatchId,
    string AgentRunId,
    IReadOnlyList<CompletedToolCallRecord> Calls,
    IReadOnlyList<CompletedToolResultRecord> Results,
    DateTimeOffset CompletedAt)
{
    public bool IsComplete =>
        Calls.Count > 0
        && Calls.Select(call => call.CallId).Distinct(StringComparer.Ordinal).Count() == Calls.Count
        && Results.Select(result => result.CallId).Distinct(StringComparer.Ordinal).Count() == Results.Count
        && Calls.Select(call => call.CallId).ToHashSet(StringComparer.Ordinal)
            .SetEquals(Results.Select(result => result.CallId));
}
