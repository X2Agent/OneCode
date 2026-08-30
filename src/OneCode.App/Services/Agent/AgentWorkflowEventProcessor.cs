using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using OneCode.Core.Coordinator;


namespace OneCode.App.Services.Agent;

/// <summary>
/// Shared helper for processing MAF agent workflow event streams.
/// Extracts the common pattern of iterating over WatchStreamAsync events,
/// collecting output text, tracking turn counts, and logging failures.
/// </summary>
internal static class AgentWorkflowEventProcessor
{
    internal sealed record ProcessResult(
        IReadOnlyList<string> OutputParts,
        int TurnsCompleted,
        bool MaxTurnsReached,
        string FinalOutput,
        long InputTokens,
        long OutputTokens,
        // 标记是否有 Agent 失败。为 true 时调用方不应提交 EditTransaction，
        // 避免半成品文件变更被持久化。
        bool HadFailures = false);

    /// <summary>
    /// Processes a MAF streaming workflow run, collecting agent responses,
    /// streaming text deltas and tool activity to the sink, and logging failures.
    /// </summary>
    /// <param name="eventStream">Async enumerable of MAF agent events to process.</param>
    /// <param name="maxTurns">Maximum number of agent turns to process.</param>
    /// <param name="failureLogMessage">Log message template used when the stream fails.</param>
    /// <param name="failureLogArgs">Arguments for the failure log message template.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="eventSink">Optional callback invoked for AgentResponseUpdateEvent (text deltas)
    /// and AgentResponseEvent (complete responses).</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task<ProcessResult> ProcessStreamAsync<TEvent>(
        IAsyncEnumerable<TEvent> eventStream,
        int maxTurns,
        string failureLogMessage,
        object?[] failureLogArgs,
        ILogger logger,
        Action<OrchestrationEvent>? eventSink,
        CancellationToken ct)
    {
        List<string> outputParts = [];
        var turnsCompleted = 0;
        var maxTurnsReached = false;
        var hadFailures = false;
        long inputTokens = 0;
        long outputTokens = 0;

        await foreach (var evt in eventStream.ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            switch (evt)
            {
                case AgentResponseUpdateEvent updateEvt when eventSink is not null:
                    var updateText = updateEvt.Update?.Text;
                    if (!string.IsNullOrEmpty(updateText))
                        eventSink(new OrchestrationEvent.TextDelta(updateEvt.ExecutorId ?? "agent", updateText!));
                    break;

                case AgentResponseEvent responseEvt:
                    var text = responseEvt.Response?.Text;
                    if (!string.IsNullOrEmpty(text))
                        outputParts.Add(text);
                    turnsCompleted++;
                    if (turnsCompleted >= maxTurns)
                        maxTurnsReached = true;

                    // 提取 token 统计（从 AgentResponse.Usage）
                    // UsageDetails.InputTokenCount/OutputTokenCount 为 long?，需 ?? 0 转 long
                    if (responseEvt.Response?.Usage is { } usage)
                    {
                        inputTokens += usage.InputTokenCount ?? 0;
                        outputTokens += usage.OutputTokenCount ?? 0;
                    }

                    if (eventSink is not null && !string.IsNullOrEmpty(text))
                        eventSink(new OrchestrationEvent.AgentMessage(responseEvt.ExecutorId ?? "agent", null, text!));
                    break;

                case ExecutorFailedEvent failEvt:
                    var args = failureLogArgs.Append(failEvt.ToString()).ToArray();
                    logger.LogWarning(failureLogMessage, args);
                    // 发射 OrchestrationEvent.Error 通知 TUI，而非仅记录日志
                    eventSink?.Invoke(new OrchestrationEvent.Error(
                        $"Team member failed: {failEvt}"));
                    // 标记发生失败，外层据此跳过 transaction.Commit()
                    hadFailures = true;
                    break;

                case WorkflowErrorEvent errorEvt:
                    // Workflow 级异常必须显式上报：此前该事件被静默丢弃，
                    // 表现为 turns=0、"(no output)"、日志无任何错误（误导为 API 配置问题）。
                    var exMessage = errorEvt.Exception?.Message ?? errorEvt.ToString();
                    logger.LogError(errorEvt.Exception, failureLogMessage, failureLogArgs.Append(exMessage).ToArray());
                    eventSink?.Invoke(new OrchestrationEvent.Error(
                        $"Team workflow failed: {exMessage}"));
                    hadFailures = true;
                    break;

                case WorkflowOutputEvent outputEvt:
                    // 工作流最终输出（Magentic orchestrator 的结论消息走此事件）：
                    // 此前被静默丢弃，导致 LLM 调用成功但 FinalOutput="(no output)"、turns=0，
                    // 被上层误报为 ChatClient 配置问题。
                    var outputText = ExtractOutputText(outputEvt.Data);
                    if (!string.IsNullOrWhiteSpace(outputText))
                    {
                        outputParts.Add(outputText);
                        eventSink?.Invoke(new OrchestrationEvent.AgentMessage(
                            outputEvt.ExecutorId ?? "workflow", null, outputText));
                    }
                    break;

                default:
                    // 诊断 turns=0：记录所有未被消费的事件类型，避免静默丢弃。
                    logger.LogDebug(
                        "Workflow event (unconsumed): {EventType} from {ExecutorId}",
                        evt?.GetType().Name ?? "(null)",
                        evt is ExecutorEvent executorEvt ? executorEvt.ExecutorId : null);
                    break;
            }
        }

        var finalOutput = outputParts.Count > 0
            ? string.Join("\n\n", outputParts)
            : "(no output)";

        return new ProcessResult(outputParts, turnsCompleted, maxTurnsReached, finalOutput, inputTokens, outputTokens, hadFailures);
    }

    /// <summary>
    /// 从 WorkflowOutputEvent.Data 提取文本。Magentic 的最终结论可能是
    /// ChatMessage、ChatMessage 集合或纯字符串，统一归一化为文本。
    /// </summary>
    private static string? ExtractOutputText(object? data) => data switch
    {
        null => null,
        string s => s,
        ChatMessage cm => string.IsNullOrWhiteSpace(cm.Text) ? null : cm.Text,
        IEnumerable<ChatMessage> msgs => string.Join("\n\n",
            msgs.Select(m => m.Text).Where(t => !string.IsNullOrWhiteSpace(t))),
        _ => data.ToString(),
    };
}
