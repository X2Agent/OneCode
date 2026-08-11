using Microsoft.Extensions.AI;

namespace OneCode.App.Services.Agent;

/// <summary>
/// Collects streaming function calls/results into sealed batches. Open batches are intentionally
/// not exposed, so cancellation or process failure cannot persist an orphaned call.
/// </summary>
internal sealed class ToolBatchCollector(string agentRunId)
{
    private readonly List<CompletedToolBatch> _completed = [];
    private readonly List<CompletedToolCallRecord> _calls = [];
    private readonly List<CompletedToolResultRecord> _results = [];
    private readonly HashSet<string> _callIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resultIds = new(StringComparer.Ordinal);
    private int _batchNumber;

    public IReadOnlyList<CompletedToolBatch> CompletedBatches => _completed;
    public bool HasOpenBatch => _calls.Count > 0;

    public void AddCall(FunctionCallContent call, string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(call.CallId) || !_callIds.Add(call.CallId))
            return;

        if (_calls.Count > 0 && _callIds.Count == _resultIds.Count)
            Seal();

        _calls.Add(new CompletedToolCallRecord(
            call.CallId,
            call.Name ?? "(unknown)",
            argumentsJson,
            _calls.Count));
    }

    public void AddResult(
        FunctionResultContent result,
        string toolName,
        string resultJson,
        bool isError,
        ToolResultCompletion completion)
    {
        if (string.IsNullOrWhiteSpace(result.CallId)
            || !_callIds.Contains(result.CallId)
            || !_resultIds.Add(result.CallId))
            return;

        _results.Add(new CompletedToolResultRecord(
            result.CallId,
            toolName,
            resultJson,
            isError,
            completion,
            _results.Count));

        if (_callIds.Count == _resultIds.Count)
            Seal();
    }

    private void Seal()
    {
        if (_calls.Count == 0 || _callIds.Count != _resultIds.Count)
            return;

        var batch = new CompletedToolBatch(
            BatchId: $"{agentRunId}:{++_batchNumber}",
            AgentRunId: agentRunId,
            Calls: _calls.ToArray(),
            Results: _results.ToArray(),
            CompletedAt: DateTimeOffset.UtcNow);

        if (!batch.IsComplete)
            throw new InvalidOperationException($"Tool batch '{batch.BatchId}' is not complete.");

        _completed.Add(batch);
        _calls.Clear();
        _results.Clear();
        _callIds.Clear();
        _resultIds.Clear();
    }
}
