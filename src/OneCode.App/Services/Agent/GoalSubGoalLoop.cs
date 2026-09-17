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
            long totalInputTokens = 0;
            long totalOutputTokens = 0;
            bool completed = false;
            int actualIterations = 0;
            int evaluatedToolExecutionCount = 0;
            string? finalOutputText = null;
            SubGoalEvidence? finalEvidence = null;
            var changeVersion = sharedTransaction.CaptureChangeVersion();

            // 用 DelegateLoopEvaluator 包装自定义评估逻辑。
            // LoopAgent 会自动将 Feedback 通过 FeedbackMessageTemplate 注入下一轮 user message，
            // 实现"执行→评估→反馈注入重试"循环，无需手写 for 循环。
            var evaluator = new DelegateLoopEvaluator(async (loopContext, evalCt) =>
            {
                actualIterations = loopContext.Iteration + 1;

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
                            totalInputTokens += (long)(details.InputTokenCount ?? 0);
                            totalOutputTokens += (long)(details.OutputTokenCount ?? 0);
                        }
                    }
                }

                var lastOutput = lastOutputBuilder.ToString();
                finalOutputText = lastOutput;

                _logger.LogInformation("Sub-goal {Id} iteration {Iteration}/{Max}: {Description}",
                    goal.Id, actualIterations, GoalLoopDefaults.MaxAttemptsPerSubGoal, goal.Description);

                // 确定性验证先于 AI Judge。硬门禁未通过时不调用模型，直接把真实证据回注下一轮。
                var iterationToolExecutions = toolExecutions.Skip(evaluatedToolExecutionCount).ToList();
                evaluatedToolExecutionCount = toolExecutions.Count;
                var hardValidation = await _hardGate.ValidateAsync(
                    goal,
                    options.WorkingDirectory,
                    sharedTransaction.GetModifiedFilesSince(changeVersion),
                    iterationToolExecutions,
                    lastOutput,
                    evalCt).ConfigureAwait(false);
                finalEvidence = hardValidation.Evidence;
                if (!hardValidation.Passed)
                {
                    _logger.LogInformation(
                        "Sub-goal {Id} deterministic validation failed at iteration {Iteration}: {Feedback}",
                        goal.Id,
                        actualIterations,
                        hardValidation.Feedback);
                    // P0：迭代重试对用户可见——硬验证未过、真实证据回注下一轮。
                    eventWriter.TryWrite(new TuiModeProgress(WorkingMode.Goal,
                        $"子目标 #{goal.Id} · 第 {actualIterations}/{GoalLoopDefaults.MaxAttemptsPerSubGoal} 轮未通过验证，反馈注入重试"));
                    return LoopEvaluation.Continue(hardValidation.Feedback);
                }

                // AI Judge 只负责语义覆盖，并必须读取真实证据摘要。
                var (isCompleted, feedback, judgeInTokens, judgeOutTokens) = await _judge.EvaluateSubGoalAsync(
                    hardValidation.Evidence, goal, evalCt).ConfigureAwait(false);
                totalInputTokens += judgeInTokens;
                totalOutputTokens += judgeOutTokens;

                if (isCompleted)
                {
                    completed = true;
                    _logger.LogInformation("Sub-goal {Id} completed after {Iterations} iterations", goal.Id, actualIterations);
                    return LoopEvaluation.Stop();
                }

                _logger.LogInformation("Sub-goal {Id} iteration {Iteration} not completed, feedback: {Feedback}",
                    goal.Id, actualIterations, feedback ?? "(none)");

                // P0：AI Judge 判定未完成——继续迭代对用户可见。
                eventWriter.TryWrite(new TuiModeProgress(WorkingMode.Goal,
                    $"子目标 #{goal.Id} · 第 {actualIterations}/{GoalLoopDefaults.MaxAttemptsPerSubGoal} 轮未完成，继续迭代"));

                // Continue(feedback) → LoopAgent 用 FeedbackMessageTemplate 将 feedback 注入下一轮
                return LoopEvaluation.Continue(feedback);
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
            var evaluatorNeverRan = actualIterations == 0;
            if (evaluatorNeverRan)
            {
                _logger.LogWarning(
                    "Sub-goal {Id} stopped before first evaluation: suspected pending tool approval " +
                    "(approval middleware missing or auto-approval rules not applied)",
                    goal.Id);
                actualIterations = 1;
            }

            goal.Status = completed ? GoalStatus.Completed : GoalStatus.Failed;
            _logger.LogInformation("Sub-goal {Id} {Status} after {Iterations} iterations",
                goal.Id, goal.Status, actualIterations);

            return new SubGoalExecution(
                GoalId: goal.Id,
                Status: goal.Status,
                Attempts: Math.Max(1, actualIterations),
                InputTokens: totalInputTokens,
                OutputTokens: totalOutputTokens,
                AgentOutput: finalOutputText ?? string.Empty,
                Evaluation: completed
                    ? "Hard validation and semantic acceptance passed"
                    : evaluatorNeverRan
                        ? "Stopped before first evaluation (suspected pending tool approval)"
                        : "Exhausted retry attempts",
                Evidence: finalEvidence);
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
