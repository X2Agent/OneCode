using System.Text;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Query;
using OneCode.App.Tui;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Agent;

/// <summary>
/// W4-C: MAF LoopAgent wiring for a single sub-goal（原生多评估器管道：
/// <c>[hardGateEvaluator, semanticJudgeEvaluator]</c> 按序短路评估；
/// 反馈注入由 LoopAgent 的聚合反馈日志承担）。
/// </summary>
internal sealed class GoalSubGoalLoop(
    GoalSubGoalHardGate hardGate,
    GoalSubGoalJudge judge,
    ILoggerFactory loggerFactory,
    TodoProjectionService? todoProjection = null)
{
    /// <summary>per-run 状态登记在 <see cref="LoopContext.AdditionalProperties"/> 上的键。</summary>
    internal const string RunStateKey = "OneCode.GoalSubGoalLoop.RunState";

    /// <summary>
    /// feedback 中回显上一轮输出的截断长度，帮助下一轮迭代快速对照上一轮总结与门禁/Judge 反馈。
    /// </summary>
    private const int LastOutputFeedbackChars = 600;

    private readonly GoalSubGoalHardGate _hardGate = hardGate;
    private readonly GoalSubGoalJudge _judge = judge;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ILogger<GoalSubGoalLoop> _logger = loggerFactory.CreateLogger<GoalSubGoalLoop>();
    private readonly TodoProjectionService? _todoProjection = todoProjection;

    /// <summary>Run MAF LoopAgent for one sub-goal with hard-gate + semantic judge.</summary>
    public async Task<SubGoalExecution> RunAsync(
        AIAgent innerAgent,
        GoalItem goal,
        GoalRunOptions options,
        EditTransaction sharedTransaction,
        ChannelWriter<TuiEvent> eventWriter,
        List<GoalToolExecutionEvidence> toolExecutions,
        string userPrompt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(eventWriter);

        try
        {
            // per-run 可变状态的唯一所有者：每次 RunAsync（= 每次 LoopAgent run）新建一个，
            // 并登记到本轮 LoopContext 上。框架要求 evaluator 不在自身上持有跨轮状态
            // （LoopContext 注释：evaluator 可能被并发 run 共享）。
            var state = new GoalLoopRunState();
            var changeVersion = sharedTransaction.CaptureChangeVersion();

            var usageTracker = new StreamingTurnUsageTracker();

            // Evaluator 1：确定性硬门禁（先于 AI Judge；未通过时由 LoopAgent 原生短路，不调模型）。
            var hardGateEvaluator = new DelegateLoopEvaluator(async (loopContext, evalCt) =>
            {
                loopContext.AdditionalProperties.TryAdd(RunStateKey, state);
                state.Iterations = loopContext.Iteration;

                // OpenAI 兼容流式端点（如 StepFun）会在每个 SSE chunk 上附带当轮累计 UsageContent，
                // LoopAgent.LastResponse.Usage 会将同一轮所有 chunk 的 UsageContent 重复累加导致虚增百倍。
                // 此处优先采用按轮次（FunctionResultContent / ResponseId 边界）去重后的真实 token 统计。
                var (iterInput, iterOutput) = usageTracker.ConsumeIterationUsage(loopContext.LastResponse.Usage);
                state.InputTokens += iterInput;
                state.OutputTokens += iterOutput;

                var lastOutput = loopContext.LastResponse.Text ?? string.Empty;
                state.FinalOutputText = lastOutput;

                _logger.LogInformation("Sub-goal {Id} iteration {Iteration}/{Max}: {Description}",
                    goal.Id, state.Iterations, GoalLoopDefaults.MaxAttemptsPerSubGoal, goal.Description);

                var iterationToolExecutions = toolExecutions.Skip(state.EvaluatedToolExecutions).ToList();
                state.EvaluatedToolExecutions = toolExecutions.Count;
                var hardValidation = await _hardGate.ValidateAsync(
                    goal,
                    options.WorkingDirectory,
                    sharedTransaction.GetModifiedFilesSince(changeVersion),
                    iterationToolExecutions,
                    lastOutput,
                    evalCt).ConfigureAwait(false);
                state.FinalEvidence = hardValidation.Evidence;
                if (!hardValidation.Passed)
                {
                    _logger.LogInformation(
                        "Sub-goal {Id} deterministic validation failed at iteration {Iteration}: {Feedback}",
                        goal.Id,
                        state.Iterations,
                        hardValidation.Feedback);
                    eventWriter.TryWrite(new TuiModeProgress(WorkingMode.Goal,
                        $"子目标 #{goal.Id} · 第 {state.Iterations}/{GoalLoopDefaults.MaxAttemptsPerSubGoal} 轮未通过验证，反馈注入重试"));
                    return LoopEvaluation.Continue(BuildFeedback(hardValidation.Feedback, lastOutput));
                }

                return LoopEvaluation.Stop();
            });

            // Evaluator 2：AI 语义验收（仅当 Evaluator 1 Stop 放行后由 LoopAgent 调用）。
            var semanticJudgeEvaluator = new DelegateLoopEvaluator(async (loopContext, evalCt) =>
            {
                var lastOutput = state.FinalOutputText ?? string.Empty;
                var (isCompleted, feedback, judgeInTokens, judgeOutTokens) = await _judge.EvaluateSubGoalAsync(
                    state.FinalEvidence!, goal, evalCt, options.WorkingDirectory).ConfigureAwait(false);
                state.InputTokens += judgeInTokens;
                state.OutputTokens += judgeOutTokens;

                if (isCompleted)
                {
                    state.Completed = true;
                    _logger.LogInformation("Sub-goal {Id} completed after {Iterations} iterations", goal.Id, state.Iterations);
                    return LoopEvaluation.Stop();
                }

                _logger.LogInformation("Sub-goal {Id} iteration {Iteration} not completed, feedback: {Feedback}",
                    goal.Id, state.Iterations, feedback ?? "(none)");

                eventWriter.TryWrite(new TuiModeProgress(WorkingMode.Goal,
                    $"子目标 #{goal.Id} · 第 {state.Iterations}/{GoalLoopDefaults.MaxAttemptsPerSubGoal} 轮未完成，继续迭代"));

                return LoopEvaluation.Continue(BuildFeedback(feedback, lastOutput));
            });

            var loopAgent = new LoopAgent(
                innerAgent,
                [hardGateEvaluator, semanticJudgeEvaluator],
                new LoopAgentOptions
                {
                    MaxIterations = GoalLoopDefaults.MaxAttemptsPerSubGoal,
                    FreshContextPerIteration = false,
                    OnBehalfOfAuthorName = "goal-judge",
                    ExcludeOnBehalfOfMessages = true,
                },
                _loggerFactory);

            // system prompt 只经 inner agent 的 ChatOptions.Instructions 下达（BuildSubGoalRunOptionsAsync
            // 已设置），此处不再放一条 System 消息。
            var messages = new List<ChatMessage>
            {
                QueryStreamHelpers.BuildUserMessage(userPrompt, options.ImagePaths, _logger),
            };

            _logger.LogInformation("Sub-goal {Id} executing with LoopAgent (max {Max} iterations): {Description}",
                goal.Id, GoalLoopDefaults.MaxAttemptsPerSubGoal, goal.Description);

            var session = await loopAgent.CreateSessionAsync(ct).ConfigureAwait(false);
            if (_todoProjection is not null)
                await _todoProjection.PublishAsync(innerAgent, session, ct).ConfigureAwait(false);

            var todoMutations = new TodoMutationObserver();
            await foreach (var update in loopAgent.RunStreamingAsync(
                messages, session, cancellationToken: ct).ConfigureAwait(false))
            {
                usageTracker.Observe(update);
                if (_todoProjection is not null && todoMutations.Observe(update))
                    await _todoProjection.PublishAsync(innerAgent, session, ct).ConfigureAwait(false);
            }

            // 若最后一轮达到 MaxIterations 先停后判未进入 evaluator，补计最后一轮未结算的流式 token。
            var (tailInput, tailOutput) = usageTracker.ConsumeIterationUsage(fallbackUsage: null);
            state.InputTokens += tailInput;
            state.OutputTokens += tailOutput;

            if (_todoProjection is not null)
                await _todoProjection.PublishAsync(innerAgent, session, ct).ConfigureAwait(false);

            // evaluator 从未触发说明 LoopAgent 在首轮就停止——最常见原因是
            // pending tool approval 无人解析（审批中间件未挂载或放行规则未生效）。
            var evaluatorNeverRan = state.EvaluatorNeverRan;
            if (evaluatorNeverRan)
            {
                _logger.LogWarning(
                    "Sub-goal {Id} stopped before first evaluation: suspected pending tool approval " +
                    "(approval middleware missing or auto-approval rules not applied)",
                    goal.Id);
                state.Iterations = 1;
            }

            goal.Status = state.Completed ? GoalStatus.Completed : GoalStatus.Failed;
            _logger.LogInformation("Sub-goal {Id} {Status} after {Iterations} iterations",
                goal.Id, goal.Status, state.Iterations);

            // LoopAgent 命中 MaxIterations 时先停后判——最后一轮不会进入评估器，
            // 因此"未完成且评估器跑过"必然意味着上限强停，真实轮次要 +1。
            var attempts = state.Completed
                ? state.Iterations
                : evaluatorNeverRan ? 1 : state.Iterations + 1;

            return new SubGoalExecution(
                GoalId: goal.Id,
                Status: goal.Status,
                Attempts: attempts,
                InputTokens: state.InputTokens,
                OutputTokens: state.OutputTokens,
                AgentOutput: state.FinalOutputText ?? string.Empty,
                Evaluation: state.Completed
                    ? "Hard validation and semantic acceptance passed"
                    : evaluatorNeverRan
                        ? "Stopped before first evaluation (suspected pending tool approval)"
                        : "Exhausted retry attempts",
                Evidence: state.FinalEvidence);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sub-goal {Id} streaming execution failed unexpectedly", goal.Id);
            goal.Status = GoalStatus.Failed;
            return new SubGoalExecution(
                GoalId: goal.Id,
                Status: GoalStatus.Failed,
                Attempts: 1,
                InputTokens: 0,
                OutputTokens: 0,
                AgentOutput: "",
                Evaluation: $"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    /// 组装注入下一轮的 feedback：先回显上一轮 agent 的实际产出摘要，
    /// 再给出本轮未通过的原因与证据。
    /// </summary>
    private static string BuildFeedback(string? outcome, string lastOutput)
    {
        var builder = new StringBuilder();
        var truncated = GoalSubGoalAssessment.TruncateForSummary(lastOutput, LastOutputFeedbackChars);
        if (!string.IsNullOrEmpty(truncated))
        {
            builder.AppendLine("## 上一轮已完成的工作");
            builder.AppendLine(truncated);
            builder.AppendLine();
        }

        builder.AppendLine("## 仍未满足");
        builder.Append(outcome);
        return builder.ToString();
    }
}

/// <summary>
/// 单个子目标循环 run 的可变状态（跨轮累加的 token、已评估轮次、最终输出与证据）。
/// 每次 <see cref="GoalSubGoalLoop.RunAsync"/> 新建一个实例并登记在本轮
/// <see cref="LoopContext.AdditionalProperties"/> 上（键 <see cref="GoalSubGoalLoop.RunStateKey"/>），
/// 因此评估器自身无跨轮字段——同一 evaluator 实例被并发 run 共享时状态不会串。
/// </summary>
internal sealed class GoalLoopRunState
{
    /// <summary>已完成的迭代次数（1-based；0 表示 evaluator 一次都没跑过）。</summary>
    public int Iterations { get; set; }

    /// <summary>累计输入 token（含 judge 调用）。</summary>
    public long InputTokens { get; set; }

    /// <summary>累计输出 token（含 judge 调用）。</summary>
    public long OutputTokens { get; set; }

    /// <summary>已计入工具证据的执行条数（下一轮从这里 Skip）。</summary>
    public int EvaluatedToolExecutions { get; set; }

    /// <summary>最后一轮 agent 输出文本。</summary>
    public string? FinalOutputText { get; set; }

    /// <summary>最近一次确定性校验产出的证据。</summary>
    public SubGoalEvidence? FinalEvidence { get; set; }

    /// <summary>是否已由硬门禁 + 语义 judge 双双接受。</summary>
    public bool Completed { get; set; }

    /// <summary>evaluator 是否从未执行（LoopAgent 首轮即停，通常是 pending tool approval）。</summary>
    public bool EvaluatorNeverRan => Iterations == 0;
}

/// <summary>
/// 按工具调用轮次（<see cref="FunctionResultContent"/> / <see cref="AgentResponseUpdate.ResponseId"/> 边界）
/// 对流式 <see cref="UsageContent"/> 去重累加，防止每个 SSE chunk 携带当轮累计 usage 的模型虚增百倍 Token。
/// </summary>
internal sealed class StreamingTurnUsageTracker
{
    private long _committedInputTokens;
    private long _committedOutputTokens;
    private long _currentTurnInputTokens;
    private long _currentTurnOutputTokens;
    private string? _currentUsageResponseId;
    private bool _usageTurnBoundaryPending;
    private bool _observedAnyInIteration;

    public void Observe(AgentResponseUpdate update)
    {
        if (update.Contents is not { Count: > 0 })
            return;

        foreach (var content in update.Contents)
        {
            if (content is FunctionResultContent && (_currentTurnInputTokens > 0 || _currentTurnOutputTokens > 0))
            {
                _usageTurnBoundaryPending = true;
            }
            else if (content is UsageContent { Details: { } details })
            {
                ObserveUsageDetails(update.ResponseId, details);
            }
        }
    }

    public (long InputTokens, long OutputTokens) ConsumeIterationUsage(UsageDetails? fallbackUsage)
    {
        if (_observedAnyInIteration)
        {
            var totalInput = _committedInputTokens + _currentTurnInputTokens;
            var totalOutput = _committedOutputTokens + _currentTurnOutputTokens;
            Reset();
            return (totalInput, totalOutput);
        }

        Reset();
        if (fallbackUsage is null)
            return (0, 0);

        return (
            Math.Max(0, fallbackUsage.InputTokenCount ?? 0),
            Math.Max(0, fallbackUsage.OutputTokenCount ?? 0));
    }

    private void ObserveUsageDetails(string? responseId, UsageDetails details)
    {
        var input = Math.Max(0, details.InputTokenCount ?? 0);
        var output = Math.Max(0, details.OutputTokenCount ?? 0);
        if (input == 0 && output == 0)
            return;

        _observedAnyInIteration = true;
        var isNewResponseId = !string.IsNullOrEmpty(responseId)
            && !string.IsNullOrEmpty(_currentUsageResponseId)
            && !string.Equals(responseId, _currentUsageResponseId, StringComparison.Ordinal);

        if ((_currentTurnInputTokens > 0 || _currentTurnOutputTokens > 0)
            && (_usageTurnBoundaryPending || isNewResponseId))
        {
            _committedInputTokens += _currentTurnInputTokens;
            _committedOutputTokens += _currentTurnOutputTokens;
            _usageTurnBoundaryPending = false;
        }

        _currentTurnInputTokens = input;
        _currentTurnOutputTokens = output;
        if (!string.IsNullOrEmpty(responseId))
            _currentUsageResponseId = responseId;
    }

    private void Reset()
    {
        _committedInputTokens = 0;
        _committedOutputTokens = 0;
        _currentTurnInputTokens = 0;
        _currentTurnOutputTokens = 0;
        _currentUsageResponseId = null;
        _usageTurnBoundaryPending = false;
        _observedAnyInIteration = false;
    }
}
