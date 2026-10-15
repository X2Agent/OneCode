using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Services.Agent;
using OneCode.Core.Coordinator;

namespace OneCode.App.Query;

/// <summary>
/// Mutable per-run streaming state: text/turn/token accumulators, CallId dedup sets,
/// the tool batch collector and the next-prompt tag parser for one agent run.
///
/// Existence rationale: an async iterator cannot share its locals with
/// helper methods, so the digest loop's ten interdependent accumulators previously
/// pinned the whole loop inside one ~460-line method. Hoisting them into this state
/// object lets <see cref="Digest"/> stay a pure (unit-testable) mapping over
/// <c>(update, session)</c> while <see cref="QueryStreamEngine"/> only orchestrates.
/// </summary>
internal sealed class StreamingSession(
    string agentRunId,
    bool includeNextPrompt,
    ILogger logger,
    Action<string>? autoActivateTool = null)
{
    private readonly StringBuilder _textBuilder = new();
    private readonly StringBuilder _lastTurnTextBuilder = new();
    // Cumulative reasoning accumulator for replay/cumulative-update collapsing (see DedupReasoning).
    private readonly StringBuilder _reasoningBuilder = new();
    private readonly Dictionary<string, string> _toolNamesByCallId = new(StringComparer.Ordinal);
    // CallId → 完整参数 JSON：ToolDoneEvent 需要它恢复 target（搜索词/路径/命令），
    // 否则完成行只剩结果摘要，同类调用无法区分。
    private readonly Dictionary<string, string> _toolArgsByCallId = new(StringComparer.Ordinal);
    // MAF may replay the same FunctionCallContent/FunctionResultContent across AgentResponseUpdate
    // boundaries (turn history replay). Without CallId-based dedup the same ToolStart/ToolDone event
    // is yielded twice, producing duplicated tool rows in the message list.
    private readonly HashSet<string> _emittedToolCallIds = new(StringComparer.Ordinal);

    private int _turnCount;
    private int _committedInputTokens;
    private int _committedOutputTokens;
    private int _committedCacheReadTokens;
    private int _committedCacheWriteTokens;
    private int _currentTurnInputTokens;
    private int _currentTurnOutputTokens;
    private int _currentTurnCacheReadTokens;
    private int _currentTurnCacheWriteTokens;
    private string? _currentUsageResponseId;
    private bool _usageTurnBoundaryPending;
    private bool _turnStarted;
    private bool _pendingTurnBoundary; // true after seeing tool results — next text starts a new turn

    /// <summary>
    /// 链路三（未知工具自愈）回调：遇到 hallucinate 但已在注册表登记的工具名时自动激活，
    /// 使下一轮工具列表包含该工具。由 engine 注入，保持本类无 engine 依赖、可独立单测。
    /// </summary>
    private readonly Action<string>? _autoActivateTool = autoActivateTool;
    private readonly ILogger _logger = logger;

    public ToolBatchCollector ToolBatchCollector { get; } = new(agentRunId);

    public NextPromptTagStreamParser? NextPromptParser { get; } =
        includeNextPrompt ? new NextPromptTagStreamParser() : null;
    public int TurnCount => _turnCount;

    public int TotalInputTokens => _committedInputTokens + _currentTurnInputTokens;

    /// <summary>最新一轮模型调用的输入 Token 水位（用于估算当前上下文窗口占用率）。</summary>
    public int LastTurnInputTokens => _currentTurnInputTokens > 0 ? _currentTurnInputTokens : TotalInputTokens;

    public string FinalText => _textBuilder.ToString();

    /// <summary>最后一轮助手回复的纯文本（不含前序工具轮的中间旁白）。</summary>
    public string LastTurnText => _lastTurnTextBuilder.ToString();

    public TokenUsage FinalUsage => new(
        _committedInputTokens + _currentTurnInputTokens,
        _committedOutputTokens + _currentTurnOutputTokens,
        CacheReadTokens: _committedCacheReadTokens + _currentTurnCacheReadTokens,
        CacheWriteTokens: _committedCacheWriteTokens + _currentTurnCacheWriteTokens);

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

        // 预算预警横幅（BudgetGuard 经 MainAgentRunner 写入）：直接透传给 TUI 消费
        if (evt is BudgetWarningEvent budgetWarning)
        {
            yield return budgetWarning;
            yield break;
        }

        // 受控 Build attempt 以 OrchestrationEvent 信封发射持久化状态
        // 投影；流内契约仍为 BuildRunStateEvent（QueryEvent），此处为运行时事件 → 流内事件
        // 的唯一投影点（原 durableStateObserver → BuildRunStateEvent 直产通道退役）。
        if (evt is OrchestrationEvent.BuildStateProjectionChanged buildProjection)
        {
            yield return BuildRunStateEvent.From(buildProjection.Run);
            yield break;
        }

        // 子代理进度旁路：fork 经 AsyncLocal 写入器上报的实时进度，投影为流内事件。
        if (evt is SubagentProgressUpdate progress)
        {
            yield return new SubagentProgressEvent(
                progress.ForkId, progress.AgentName, progress.Phase,
                progress.ToolCallCount, progress.TurnCount,
                progress.CurrentToolName, progress.ElapsedMs);
            yield break;
        }

        if (evt is not AgentResponseUpdate update)
            yield break;

        if (AgentEventDigester.TryExtractUsage(update, out var usage))
        {
            var isNewResponseId = !string.IsNullOrEmpty(update.ResponseId)
                && !string.IsNullOrEmpty(_currentUsageResponseId)
                && !string.Equals(update.ResponseId, _currentUsageResponseId, StringComparison.Ordinal);

            if ((_currentTurnInputTokens > 0 || _currentTurnOutputTokens > 0)
                && (_usageTurnBoundaryPending || isNewResponseId))
            {
                _committedInputTokens += _currentTurnInputTokens;
                _committedOutputTokens += _currentTurnOutputTokens;
                _committedCacheReadTokens += _currentTurnCacheReadTokens;
                _committedCacheWriteTokens += _currentTurnCacheWriteTokens;
                _usageTurnBoundaryPending = false;
            }

            _currentTurnInputTokens = usage.InputTokens;
            _currentTurnOutputTokens = usage.OutputTokens;
            _currentTurnCacheReadTokens = usage.CacheReadTokens;
            _currentTurnCacheWriteTokens = usage.CacheWriteTokens;
            if (!string.IsNullOrEmpty(update.ResponseId))
                _currentUsageResponseId = update.ResponseId;

            yield return new UsageUpdateEvent(FinalUsage);
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
                    {
                        ToolBatchCollector.AddCall(fcc, serializedArgs);
                        if (fcc.CallId is not null)
                            _toolArgsByCallId[fcc.CallId] = serializedArgs;
                    }
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
                    _toolArgsByCallId.TryGetValue(frc.CallId ?? "", out var doneToolInput);
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
                        resultText,
                        doneToolInput);
                    _pendingTurnBoundary = true;
                    if (_currentTurnInputTokens > 0 || _currentTurnOutputTokens > 0)
                        _usageTurnBoundaryPending = true;
                }
                else if (c is TextReasoningContent trc && !string.IsNullOrEmpty(trc.Text))
                {
                    // Reasoning needs the same replay protection as the tool events above:
                    // providers may resend reasoning cumulatively ("so far" per update) and
                    // MAF may replay it across turn-boundary updates. Collapsing to true
                    // increments keeps the TUI's append-only ThinkingBuffer free of duplicates.
                    foreach (var delta in DedupReasoning(trc.Text))
                        yield return new ThinkingDeltaEvent(delta);
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

            foreach (var (rawText, suggestion) in segments)
            {
                var text = rawText;
                if (!string.IsNullOrEmpty(text))
                {
                    if (!_turnStarted)
                    {
                        // 部分推理模型在 TextReasoningContent 之后的首段正文前吐出数十个前导空格/换行；
                        // 在轮次起点统一剥离，全空白段不开启新轮次（下一段继续按起点处理）。
                        text = text.TrimStart(' ', '\t', '\r', '\n');
                        if (text.Length == 0)
                        {
                            if (!string.IsNullOrEmpty(suggestion))
                                yield return new SuggestionsEvent([suggestion]);
                            continue;
                        }

                        _turnCount++;
                        _turnStarted = true;
                        _lastTurnTextBuilder.Clear();
                        if (_textBuilder.Length > 0 && _textBuilder[^1] != '\n')
                            _textBuilder.Append("\n\n");
                        yield return new TurnStartedEvent(_turnCount);
                    }

                    _textBuilder.Append(text);
                    _lastTurnTextBuilder.Append(text);
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

        var text = remainingText;
        if (!_turnStarted)
        {
            text = text.TrimStart(' ', '\t', '\r', '\n');
            if (text.Length == 0)
                yield break;

            _turnCount++;
            _turnStarted = true;
            _lastTurnTextBuilder.Clear();
            if (_textBuilder.Length > 0 && _textBuilder[^1] != '\n')
                _textBuilder.Append("\n\n");
            yield return new TurnStartedEvent(_turnCount);
        }

        _textBuilder.Append(text);
        _lastTurnTextBuilder.Append(text);
        yield return new TextDeltaEvent(text);
    }

    /// <summary>
    /// Collapses cumulative/replayed reasoning into true increments. Replay forms seen in
    /// the wild: exact resend of everything streamed so far, resend of an older prefix,
    /// or a previously-streamed prefix extended by new text (cumulative "so far" updates).
    /// Only the unseen suffix is emitted; genuine disjoint deltas fall through unchanged.
    /// The accumulator is per-run (never reset at turn boundaries): a boundary reset would
    /// re-emit reasoning that MAF replays right after a tool result — the exact duplication
    /// this guard exists to prevent.
    /// </summary>
    /// <remarks>
    /// 前缀比较全程经 <see cref="StringBuilder.GetChunks"/> 逐段进行，不物化聚合串：
    /// 每个增量一次全量 <c>ToString</c> 会让长 reasoning 的去重退化为 O(n²) 分配与拷贝。
    /// </remarks>
    private IEnumerable<string> DedupReasoning(string text)
    {
        if (text.Length == 0)
            yield break; // 空增量：既无新内容，也不构成任何重放形态
        if (_reasoningBuilder.Length == 0)
        {
            _reasoningBuilder.Append(text);
            yield return text;
            yield break;
        }
        if (AggregateStartsWith(text))
            yield break; // 完全重放或旧前缀重放——无新内容
        if (TextStartsWithAggregate(text))
        {
            // Cumulative update (or replay + extension): emit only the new suffix.
            var increment = text[_reasoningBuilder.Length..];
            _reasoningBuilder.Append(increment);
            yield return increment;
            yield break;
        }
        // Unrelated segment: a genuine disjoint delta or a new turn's reasoning.
        _reasoningBuilder.Append(text);
        yield return text;
    }

    /// <summary>聚合内容是否以 <paramref name="text"/> 为前缀（含完全相等）。</summary>
    private bool AggregateStartsWith(string text)
    {
        if (text.Length > _reasoningBuilder.Length)
            return false;
        var pos = 0;
        foreach (var chunk in _reasoningBuilder.GetChunks())
        {
            var take = Math.Min(chunk.Span.Length, text.Length - pos);
            if (!chunk.Span[..take].SequenceEqual(text.AsSpan(pos, take)))
                return false;
            pos += take;
            if (pos == text.Length)
                return true;
        }
        return pos == text.Length;
    }

    /// <summary><paramref name="text"/> 是否以聚合内容为前缀（累积更新形态，text 比聚合长）。</summary>
    private bool TextStartsWithAggregate(string text)
    {
        if (text.Length < _reasoningBuilder.Length)
            return false;
        var pos = 0;
        foreach (var chunk in _reasoningBuilder.GetChunks())
        {
            if (!chunk.Span.SequenceEqual(text.AsSpan(pos, chunk.Span.Length)))
                return false;
            pos += chunk.Span.Length;
        }
        return true;
    }

    /// <summary>Returns the turn-completion event if any turn was started during the run.</summary>
    public TurnCompletedEvent? CompleteTurnIfStarted()
        => _turnStarted ? new TurnCompletedEvent(_turnCount, false) : null;
}
