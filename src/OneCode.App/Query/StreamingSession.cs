using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Services.Agent;

namespace OneCode.App.Query;

/// <summary>
/// Mutable per-run streaming state: text/turn/token accumulators, CallId dedup sets,
/// the tool batch collector and the next-prompt tag parser for one agent run.
///
/// Existence rationale (ADR 0006): an async iterator cannot share its locals with
/// helper methods, so the digest loop's ten interdependent accumulators previously
/// pinned the whole loop inside one ~460-line method. Hoisting them into this state
/// object lets <see cref="Digest"/> stay a pure (unit-testable) mapping over
/// <c>(update, session)</c> while <see cref="QueryStreamEngine"/> only orchestrates.
/// </summary>
internal sealed class StreamingSession
{
    private readonly StringBuilder _textBuilder = new();
    private readonly Dictionary<string, string> _toolNamesByCallId = new(StringComparer.Ordinal);
    // MAF may replay the same FunctionCallContent/FunctionResultContent across AgentResponseUpdate
    // boundaries (turn history replay). Without CallId-based dedup the same ToolStart/ToolDone event
    // is yielded twice, producing duplicated tool rows in the message list.
    private readonly HashSet<string> _emittedToolCallIds = new(StringComparer.Ordinal);

    private int _turnCount;
    private int _totalInputTokens;
    private int _totalOutputTokens;
    private int _totalCacheReadTokens;
    private int _totalCacheWriteTokens;
    private bool _turnStarted;
    private bool _pendingTurnBoundary; // true after seeing tool results — next text starts a new turn

    /// <summary>
    /// 链路三（未知工具自愈）回调：遇到 hallucinate 但已在注册表登记的工具名时自动激活，
    /// 使下一轮工具列表包含该工具。由 engine 注入，保持本类无 engine 依赖、可独立单测。
    /// </summary>
    private readonly Action<string>? _autoActivateTool;
    private readonly ILogger _logger;

    public StreamingSession(
        string agentRunId,
        bool includeNextPrompt,
        ILogger logger,
        Action<string>? autoActivateTool = null)
    {
        ToolBatchCollector = new ToolBatchCollector(agentRunId);
        NextPromptParser = includeNextPrompt ? new NextPromptTagStreamParser() : null;
        _logger = logger;
        _autoActivateTool = autoActivateTool;
    }

    public ToolBatchCollector ToolBatchCollector { get; }

    public NextPromptTagStreamParser? NextPromptParser { get; }

    public int TurnCount => _turnCount;

    public int TotalInputTokens => _totalInputTokens;

    public string FinalText => _textBuilder.ToString();

    public TokenUsage FinalUsage => new(
        _totalInputTokens,
        _totalOutputTokens,
        CacheReadTokens: _totalCacheReadTokens,
        CacheWriteTokens: _totalCacheWriteTokens);

    /// <summary>
    /// Maps one raw channel event (approval passthrough, BuildRun state, or MAF
    /// <see cref="AgentResponseUpdate"/>) onto query events, mutating accumulators.
    /// Pure CPU logic — no awaits, no engine dependencies — so it is directly unit-testable.
    /// </summary>
    public IEnumerable<QueryEvent> Digest(object evt)
    {
        // 事件驱动审批：ApprovalRequestEvent 直接透传给 TUI 消费
        if (evt is ApprovalRequestEvent approvalEvt)
        {
            yield return approvalEvt;
            yield break;
        }

        if (evt is BuildRunStateEvent buildStateEvent)
        {
            yield return buildStateEvent;
            yield break;
        }

        if (evt is not AgentResponseUpdate update)
            yield break;

        if (AgentEventDigester.TryExtractUsage(update, out var usage))
        {
            _totalInputTokens = usage.InputTokens;
            _totalOutputTokens = usage.OutputTokens;
            _totalCacheReadTokens = usage.CacheReadTokens;
            _totalCacheWriteTokens = usage.CacheWriteTokens;
            yield return new UsageUpdateEvent(usage);
        }

        // 检测工具调用、工具结果、推理内容，发射对应事件
        if (update.Contents is { Count: > 0 })
        {
            foreach (var c in update.Contents)
            {
                if (c is FunctionCallContent fcc)
                {
                    var toolName = fcc.Name ?? "(unknown)";
                    if (fcc.CallId is not null)
                        _toolNamesByCallId[fcc.CallId] = toolName;

                    // 链路三：未知工具兜底——模型 hallucinate 的工具名若在注册表中存在但未加载，
                    // 自动激活它，使下一轮工具列表包含该工具。
                    _autoActivateTool?.Invoke(toolName);

                    // Skip duplicates replayed by MAF across turn-boundary updates
                    if (fcc.CallId is null || !_emittedToolCallIds.Add("start:" + fcc.CallId))
                        continue;
                    var toolInput = AgentEventDigester.ExtractToolInputSummary(fcc, _logger);
                    // Collect for persistence so /files can extract paths.
                    // Serialization failure must not forge "{}" — skip persistence of that block.
                    var serializedArgs = AgentEventDigester.SerializeArguments(fcc.Arguments, _logger);
                    if (serializedArgs is not null)
                        ToolBatchCollector.AddCall(fcc, serializedArgs);
                    yield return new ToolStartEvent(
                        fcc.CallId ?? "",
                        toolName,
                        toolInput);
                }
                else if (c is FunctionResultContent frc)
                {
                    // Skip duplicates replayed by MAF across turn-boundary updates
                    if (frc.CallId is null || !_emittedToolCallIds.Add("done:" + frc.CallId))
                        continue;
                    var (isError, resultText) = AgentEventDigester.ExtractToolResult(frc, _logger);
                    var name = (frc.CallId is not null
                        && _toolNamesByCallId.TryGetValue(frc.CallId, out var n)) ? n : "(unknown)";
                    ToolBatchCollector.AddResult(
                        frc,
                        name,
                        resultText ?? "null",
                        isError,
                        isError ? ToolResultCompletion.Failed : ToolResultCompletion.Succeeded);
                    yield return new ToolDoneEvent(
                        frc.CallId ?? "",
                        name,
                        isError,
                        resultText);
                    _pendingTurnBoundary = true;
                }
                else if (c is TextReasoningContent trc && !string.IsNullOrEmpty(trc.Text))
                {
                    yield return new ThinkingDeltaEvent(trc.Text);
                }
            }
        }

        // Detect turn boundaries: new assistant text after tool results = new turn
        if (_pendingTurnBoundary && !string.IsNullOrEmpty(update.Text))
        {
            _pendingTurnBoundary = false;
            _turnStarted = false; // force new turn on next text
        }

        if (!string.IsNullOrEmpty(update.Text))
        {
            var segments = NextPromptParser is null
                ? [(Text: update.Text, Suggestion: (string?)null)]
                : NextPromptParser.Process(update.Text);

            foreach (var (text, suggestion) in segments)
            {
                if (!string.IsNullOrEmpty(text))
                {
                    _textBuilder.Append(text);
                    if (!_turnStarted)
                    {
                        _turnCount++;
                        _turnStarted = true;
                        yield return new TurnStartedEvent(_turnCount);
                    }
                    yield return new TextDeltaEvent(text);
                }

                if (!string.IsNullOrEmpty(suggestion))
                    yield return new SuggestionsEvent([suggestion]);
            }
        }
    }

    /// <summary>
    /// Emits leftover buffered markup from the next-prompt parser (an interrupted
    /// response loses no content) as final text events.
    /// </summary>
    public IEnumerable<QueryEvent> FlushTrailingText()
    {
        if (NextPromptParser?.Flush() is not { Length: > 0 } remainingText)
            yield break;

        _textBuilder.Append(remainingText);
        if (!_turnStarted)
        {
            _turnCount++;
            _turnStarted = true;
            yield return new TurnStartedEvent(_turnCount);
        }
        yield return new TextDeltaEvent(remainingText);
    }

    /// <summary>Returns the turn-completion event if any turn was started during the run.</summary>
    public TurnCompletedEvent? CompleteTurnIfStarted()
        => _turnStarted ? new TurnCompletedEvent(_turnCount, false) : null;
}
