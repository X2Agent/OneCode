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
    private bool _pendingTurnBoundary;
    private bool _turnStarted;

    public int TurnCount { get; private set; }
    public int InputTokens { get; private set; }
    public int OutputTokens { get; private set; }
    public bool BudgetExceeded { get; private set; }
    public IReadOnlyList<CompletedToolBatch> CompletedToolBatches => _toolBatches.CompletedBatches;

    public void Observe(AgentResponseUpdate update)
    {
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
    }

    private void ObserveUsage(AgentResponseUpdate update)
    {
        var details = update.Contents?.OfType<UsageContent>().FirstOrDefault()?.Details;
        if (details is null)
            return;
        InputTokens = SafeInt(details.InputTokenCount);
        OutputTokens = SafeInt(details.OutputTokenCount);
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
                string text => (false, text),
                ToolResult toolResult => (toolResult.IsError, ToolResultSerializer.Serialize(toolResult)),
                null => (false, null),
                _ => (false, Truncate(JsonSerializer.Serialize(result, JsonOptions), 500)),
            };
        }
        catch (NotSupportedException)
        {
            return (false, null);
        }
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
