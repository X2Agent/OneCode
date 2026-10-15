using System.Text.Encodings.Web;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace OneCode.App.Services.Agent;

/// <summary>Collects durable BuildRun evidence at the source before streaming events reach UI consumers.</summary>
internal sealed class MainAgentRunEvidenceCollector(string agentRunId)
{
    private readonly ToolBatchCollector _toolBatches = new(agentRunId);
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _observedToolEvents = new(StringComparer.Ordinal);
    private int _committedInputTokens;
    private int _committedOutputTokens;
    private int _currentTurnInputTokens;
    private int _currentTurnOutputTokens;
    private string? _currentUsageResponseId;
    private bool _usageTurnBoundaryPending;
    private bool _pendingTurnBoundary;
    private bool _turnStarted;

    public int TurnCount { get; private set; }
    public int ToolCallCount { get; private set; }
    public int InputTokens => _committedInputTokens + _currentTurnInputTokens;
    public int OutputTokens => _committedOutputTokens + _currentTurnOutputTokens;
    public ChatFinishReason? FinishReason { get; private set; }
    public bool BudgetExceeded { get; private set; }
    public IReadOnlyList<CompletedToolBatch> CompletedToolBatches => _toolBatches.CompletedBatches;

    public void Observe(AgentResponseUpdate update)
    {
        FinishReason = update.FinishReason ?? FinishReason;
        ObserveUsage(update);
        if (update.Contents is { Count: > 0 })
        {
            foreach (var content in update.Contents)
            {
                if (content is FunctionCallContent call)
                    ObserveCall(call);
                else if (content is FunctionResultContent result)
                    ObserveResult(result);
            }
        }

        if (_pendingTurnBoundary && !string.IsNullOrEmpty(update.Text))
        {
            _pendingTurnBoundary = false;
            _turnStarted = false;
        }
        if (!string.IsNullOrEmpty(update.Text))
        {
            if (!_turnStarted)
            {
                TurnCount++;
                _turnStarted = true;
            }
            if (update.Text.Contains("[Budget Exceeded]", StringComparison.OrdinalIgnoreCase))
                BudgetExceeded = true;
        }
    }

    private void ObserveCall(FunctionCallContent call)
    {
        if (string.IsNullOrWhiteSpace(call.CallId)
            || !_observedToolEvents.Add("call:" + call.CallId))
        {
            return;
        }
        ToolCallCount++;
        _toolNames[call.CallId] = call.Name ?? "(unknown)";
        var arguments = Serialize(call.Arguments);
        if (arguments is not null)
            _toolBatches.AddCall(call, arguments);
    }

    private void ObserveResult(FunctionResultContent result)
    {
        if (string.IsNullOrWhiteSpace(result.CallId)
            || !_observedToolEvents.Add("result:" + result.CallId))
        {
            return;
        }
        var (isError, value) = SerializeResult(result.Result);
        var toolName = _toolNames.GetValueOrDefault(result.CallId, "(unknown)");
        _toolBatches.AddResult(
            result,
            toolName,
            value ?? "null",
            isError,
            isError ? ToolResultCompletion.Failed : ToolResultCompletion.Succeeded);
        _pendingTurnBoundary = true;
        if (_currentTurnInputTokens > 0 || _currentTurnOutputTokens > 0)
            _usageTurnBoundaryPending = true;
    }

    private void ObserveUsage(AgentResponseUpdate update)
    {
        var details = update.Contents?.OfType<UsageContent>().FirstOrDefault()?.Details;
        if (details is null)
            return;
        var input = SafeInt(details.InputTokenCount);
        var output = SafeInt(details.OutputTokenCount);
        if (input == 0 && output == 0)
            return;

        var isNewResponseId = !string.IsNullOrEmpty(update.ResponseId)
            && !string.IsNullOrEmpty(_currentUsageResponseId)
            && !string.Equals(update.ResponseId, _currentUsageResponseId, StringComparison.Ordinal);

        if ((_currentTurnInputTokens > 0 || _currentTurnOutputTokens > 0)
            && (_usageTurnBoundaryPending || isNewResponseId))
        {
            _committedInputTokens += _currentTurnInputTokens;
            _committedOutputTokens += _currentTurnOutputTokens;
            _usageTurnBoundaryPending = false;
        }

        _currentTurnInputTokens = input;
        _currentTurnOutputTokens = output;
        if (!string.IsNullOrEmpty(update.ResponseId))
            _currentUsageResponseId = update.ResponseId;
    }

    private static string? Serialize(object? value)
    {
        if (value is null)
            return "{}";
        try
        {
            return JsonSerializer.Serialize(value, JsonOptions);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static (bool IsError, string? Value) SerializeResult(object? result)
    {
        try
        {
            return result switch
            {
                Exception ex => (true, ex.Message),
                string text => DisplayJsonSerializer.TryUnwrapEnvelope(text, out var strIsError, out var innerText)
                    ? (strIsError, innerText)
                    : (strIsError, text),
                ToolResult toolResult => (toolResult.IsError, ToolResultSerializer.Serialize(toolResult)),
                null => (false, null),
                _ => SerializeArbitraryResult(result),
            };
        }
        catch (NotSupportedException)
        {
            return (false, null);
        }
    }

    private static (bool IsError, string? Value) SerializeArbitraryResult(object result)
    {
        var json = JsonSerializer.Serialize(result, JsonOptions);
        if (DisplayJsonSerializer.TryUnwrapEnvelope(json, out var isError, out var inner))
            return (isError, Truncate(inner, 500));

        return (isError, Truncate(json, 500));
    }

    private static int SafeInt(long? value)
        => value is null or 0 ? 0 : value > int.MaxValue ? int.MaxValue : (int)value.Value;

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 3)] + "...";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };
}
