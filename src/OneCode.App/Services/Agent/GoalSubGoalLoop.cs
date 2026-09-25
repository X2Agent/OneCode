using System.Text;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Tui;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Agent;

/// <summary>
/// W4-C: MAF LoopAgent wiring for a single sub-goal (DelegateLoopEvaluator + FeedbackMessageTemplate).
/// </summary>
internal sealed class GoalSubGoalLoop(
    GoalSubGoalHardGate hardGate,
    GoalSubGoalJudge judge,
    ILoggerFactory loggerFactory)
{
    /// <summary>per-run 状态登记在 <see cref="LoopContext.AdditionalProperties"/> 上的键。</summary>
    internal const string RunStateKey = "OneCode.GoalSubGoalLoop.RunState";

    /// <summary>
    /// feedback 中回显上一轮输出的截断长度。<see cref="LoopAgentOptions.FreshContextPerIteration"/>
    /// 下 inner agent 对上一轮产出零记忆，不回显就会重复已完成的动作
    /// （对齐 MAF <c>CompletionMarkerLoopEvaluator.LastResponsePlaceholder</c> 的用意）。
    /// </summary>
    private const int LastOutputFeedbackChars = 600;

    private readonly GoalSubGoalHardGate _hardGate = hardGate;
    private readonly GoalSubGoalJudge _judge = judge;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ILogger<GoalSubGoalLoop> _logger = loggerFactory.CreateLogger<GoalSubGoalLoop>();

    /// <summary>Run MAF LoopAgent for one sub-goal with hard-gate + semantic judge.</summary>
    public async Task<SubGoalExecution> RunAsync(
        AIAgent innerAgent,
        GoalItem goal,
        GoalRunOptions options,
        EditTransaction sharedTransaction,
        ChannelWriter<TuiEvent> eventWriter,
        List<GoalToolExecutionEvidence> toolExecutions,
        string systemPrompt,
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

            // 用 DelegateLoopEvaluator 包装自定义评估逻辑。
            // LoopAgent 会自动将 Feedback 通过 FeedbackMessageTemplate 注入下一轮 user message，
            // 实现"执行→评估→反馈注入重试"循环，无需手写 for 循环。
            var evaluator = new DelegateLoopEvaluator(async (loopContext, evalCt) =>
            {
                loopContext.AdditionalProperties.TryAdd(RunStateKey, state);
                state.Iterations = loopContext.Iteration;

                // 从 LastResponse 提取本轮输出文本和 token（FreshContextPerIteration=true
                // 保证每轮 session 独立，不会跨轮累积输出）
                var lastOutputBuilder = new StringBuilder();
                foreach (var message in loopContext.LastResponse.Messages)
                {
                    if (message.Role != ChatRole.Assistant) continue;
                    foreach (var content in message.Contents)
                    {
                        if (content is TextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                        {
                            lastOutputBuilder.Append(textContent.Text);
                        }
                        else if (content is UsageContent usageContent && usageContent.Details is { } details)
                        {
                            state.InputTokens += (long)(details.InputTokenCount ?? 0);
                            state.OutputTokens += (long)(details.OutputTokenCount ?? 0);
                        }
                    }
                }

                var lastOutput = lastOutputBuilder.ToString();
                state.FinalOutputText = lastOutput;

                _logger.LogInformation("Sub-goal {Id} iteration {Iteration}/{Max}: {Description}",
                    goal.Id, state.Iterations, GoalLoopDefaults.MaxAttemptsPerSubGoal, goal.Description);

                // 确定性验证先于 AI Judge。硬门禁未通过时不调用模型，直接把真实证据回注下一轮。
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
                    // P0：迭代重试对用户可见——硬验证未过、真实证据回注下一轮。
                    eventWriter.TryWrite(new TuiModeProgress(WorkingMode.Goal,
                        $"子目标 #{goal.Id} · 第 {state.Iterations}/{GoalLoopDefaults.MaxAttemptsPerSubGoal} 轮未通过验证，反馈注入重试"));
                    return LoopEvaluation.Continue(BuildFeedback(hardValidation.Feedback, lastOutput));
                }

                // AI Judge 只负责语义覆盖，并必须读取真实证据摘要。
                var (isCompleted, feedback, judgeInTokens, judgeOutTokens) = await _judge.EvaluateSubGoalAsync(
                    hardValidation.Evidence, goal, evalCt).ConfigureAwait(false);
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

                // P0：AI Judge 判定未完成——继续迭代对用户可见。
                eventWriter.TryWrite(new TuiModeProgress(WorkingMode.Goal,
                    $"子目标 #{goal.Id} · 第 {state.Iterations}/{GoalLoopDefaults.MaxAttemptsPerSubGoal} 轮未完成，继续迭代"));

                // Continue(feedback) → LoopAgent 用 FeedbackMessageTemplate 将 feedback 注入下一轮
                return LoopEvaluation.Continue(BuildFeedback(feedback, lastOutput));
            });

            var loopAgent = new LoopAgent(
                innerAgent,
                evaluator,
                new LoopAgentOptions
                {
                    MaxIterations = GoalLoopDefaults.MaxAttemptsPerSubGoal,
                    FreshContextPerIteration = true,
                    OnBehalfOfAuthorName = "goal-judge",
                    ExcludeOnBehalfOfMessages = true,
                },
                _loggerFactory);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt ?? string.Empty),
                BuildUserMessage(userPrompt, options.ImagePaths),
            };

            _logger.LogInformation("Sub-goal {Id} executing with LoopAgent (max {Max} iterations): {Description}",
                goal.Id, GoalLoopDefaults.MaxAttemptsPerSubGoal, goal.Description);

            await foreach (var update in loopAgent.RunStreamingAsync(
                messages, cancellationToken: ct).ConfigureAwait(false))
            {
                // Agent text remains in the MAF response and evaluation evidence.
                // The Goal transcript intentionally exposes only projected progress,
                // tool activity and file changes rather than every model token.
                _ = update;
            }

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
    /// 组装注入下一轮的 feedback：先回显上一轮 agent 的实际产出（fresh context 下它对此零记忆），
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

    private ChatMessage BuildUserMessage(string prompt, IReadOnlyList<string>? imagePaths)
    {
        if (imagePaths is not { Count: > 0 })
            return new ChatMessage(ChatRole.User, prompt);

        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(prompt))
            contents.Add(new TextContent(prompt));

        foreach (var path in imagePaths)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var mediaType = ext switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    ".bmp" => "image/bmp",
                    _ => "image/png",
                };
                contents.Add(new DataContent(bytes, mediaType));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load goal image attachment {Path}", path);
                contents.Add(new TextContent($"[Failed to load image: {Path.GetFileName(path)}]"));
            }
        }

        return new ChatMessage(ChatRole.User, contents);
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
