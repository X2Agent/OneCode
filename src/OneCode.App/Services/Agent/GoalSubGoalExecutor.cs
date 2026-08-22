using System.Text;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Services.GoalMode;
using OneCode.App.Services.Lsp;
using OneCode.App.Tui;
using OneCode.Core.Coordinator;
using OneCode.Core.IO;
using OneCode.Core.Lsp;
using OneCode.Core.Prompt;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Agent;

/// <summary>
/// GOAL 模式子目标执行器：使用 MAF <see cref="LoopAgent"/> + <see cref="DelegateLoopEvaluator"/> 实现
/// 「执行→评估→反馈注入重试」循环。
///
/// <para>
/// 架构说明：
///   - <see cref="MainAgentRunner.BuildAsAIAgentAsync"/> 构建已装配全部中间件的 AIAgent
///   - <see cref="DelegateLoopEvaluator"/> 包装 <c>EvaluateSubGoalAsync</c> 逻辑，返回 <see cref="LoopEvaluation"/>
///   - <see cref="LoopAgent"/> 在 <c>ShouldReinvoke=true</c> 时，用 <c>FeedbackMessageTemplate</c> 将
///     <see cref="LoopEvaluation.Feedback"/> 格式化为 user message 注入下一轮——实现反馈注入重试
/// </para>
///
/// <para>
/// <b>为何使用 <see cref="DelegateLoopEvaluator"/> 而非 <see cref="AIJudgeLoopEvaluator"/></b>：
/// <see cref="AIJudgeLoopEvaluator"/> 的 judge prompt 和 verdict 解析逻辑是 MAF 内置的，
/// 无法定制。本项目需要 judge 返回结构化的 gap 反馈且复用已有的 <c>EvaluateSubGoalAsync</c> 逻辑
/// （包含成功标准动态注入、VERDICT 标记解析等），因此用 <see cref="DelegateLoopEvaluator"/> 包装自定义逻辑。
/// 两者都通过 <see cref="LoopEvaluation.Feedback"/> + <c>FeedbackMessageTemplate</c> 实现反馈注入，机制一致。
/// </para>
/// </summary>
internal interface IGoalStepExecutionService
{
    Task<SubGoalExecution> ExecuteSubGoalWithLoopStreamingAsync(
        GoalItem goal,
        GoalRunOptions options,
        EditTransaction sharedTransaction,
        ChannelWriter<TuiEvent> eventWriter,
        CancellationToken ct);
    void UpdateGoalContext(
        GoalPlan plan,
        GoalItem currentGoal,
        IReadOnlyList<SubGoalExecution> executions,
        bool sharedTransactionOwned);
    Task<(bool Passed, string Summary, long InputTokens, long OutputTokens)> EvaluateFinalGoalAsync(
        string originalGoal,
        IReadOnlyList<GoalItem> goals,
        IReadOnlyList<SubGoalExecution> executions,
        CancellationToken ct);
}

internal sealed class GoalSubGoalExecutor : IGoalStepExecutionService
{
    private readonly IMainAgentRunner _mainAgentRunner;
    private readonly IChatClient _chatClient;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<GoalSubGoalExecutor> _logger;
    private readonly IPromptManager _promptManager;
    private readonly GoalContextState _goalContextState;
    private readonly IVerificationProvider? _verificationProvider;
    private readonly LspDiagnosticRegistry? _diagnosticRegistry;

    /// <summary>单个子目标最多重试次数（对标 <see cref="LoopAgentOptions.MaxIterations"/>）。</summary>
    public const int MaxAttemptsPerSubGoal = 3;

    public GoalSubGoalExecutor(
        IMainAgentRunner mainAgentRunner,
        IChatClient chatClient,
        ILoggerFactory loggerFactory,
        ILogger<GoalSubGoalExecutor> logger,
        IPromptManager promptManager,
        GoalContextState goalContextState,
        IVerificationProvider? verificationProvider = null,
        LspDiagnosticRegistry? diagnosticRegistry = null)
    {
        _mainAgentRunner = mainAgentRunner;
        _chatClient = chatClient;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _promptManager = promptManager;
        _goalContextState = goalContextState;
        _verificationProvider = verificationProvider;
        _diagnosticRegistry = diagnosticRegistry;
    }

    /// <summary>
    /// 执行子目标循环（流式版本）。
    ///
    /// <para>
    /// 使用 MAF <see cref="LoopAgent"/> + <see cref="DelegateLoopEvaluator"/> 管理迭代与反馈注入：
    /// <list type="bullet">
    ///   <item><see cref="LoopAgent"/> 在每轮 agent 执行后调用 evaluator</item>
    ///   <item>evaluator 返回 <see cref="LoopEvaluation"/>：
    ///     <c>ShouldReinvoke=false</c> → 完成；<c>ShouldReinvoke=true</c> + <c>Feedback</c> → 重试</item>
    ///   <item>重试时 <see cref="LoopAgent"/> 用 <c>FeedbackMessageTemplate</c> 将 <c>Feedback</c> 注入下一轮 user message</item>
    ///   <item><c>FreshContextPerIteration=true</c> 每轮重置 session，避免输出跨轮累积</item>
    /// </list>
    /// </para>
    /// </summary>
    public async Task<SubGoalExecution> ExecuteSubGoalWithLoopStreamingAsync(
        GoalItem goal,
        GoalRunOptions options,
        EditTransaction sharedTransaction,
        ChannelWriter<TuiEvent> eventWriter,
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
            var toolExecutions = new List<GoalToolExecutionEvidence>();
            var changeVersion = sharedTransaction.CaptureChangeVersion();

            var userPrompt = await BuildSubGoalPromptAsync(goal, ct).ConfigureAwait(false);
            var runOptions = await BuildSubGoalRunOptionsAsync(
                goal,
                options,
                sharedTransaction,
                userPrompt,
                evt => CaptureToolEvidence(evt, toolExecutions),
                ct).ConfigureAwait(false);

            var innerAgent = await _mainAgentRunner.BuildAsAIAgentAsync(runOptions, ct).ConfigureAwait(false);

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
                    goal.Id, actualIterations, MaxAttemptsPerSubGoal, goal.Description);

                // 确定性验证先于 AI Judge。硬门禁未通过时不调用模型，直接把真实证据回注下一轮。
                var iterationToolExecutions = toolExecutions.Skip(evaluatedToolExecutionCount).ToList();
                evaluatedToolExecutionCount = toolExecutions.Count;
                var hardValidation = await ValidateSubGoalAsync(
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
                    return LoopEvaluation.Continue(hardValidation.Feedback);
                }

                // AI Judge 只负责语义覆盖，并必须读取真实证据摘要。
                var (isCompleted, feedback, judgeInTokens, judgeOutTokens) = await EvaluateSubGoalAsync(
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

                // Continue(feedback) → LoopAgent 用 FeedbackMessageTemplate 将 feedback 注入下一轮
                return LoopEvaluation.Continue(feedback);
            });

            var loopAgent = new LoopAgent(
                innerAgent,
                evaluator,
                new LoopAgentOptions
                {
                    MaxIterations = MaxAttemptsPerSubGoal,
                    FreshContextPerIteration = true,
                    OnBehalfOfAuthorName = "goal-judge",
                    ExcludeOnBehalfOfMessages = true,
                },
                _loggerFactory);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, runOptions.SystemPrompt ?? string.Empty),
                BuildUserMessage(userPrompt, options.ImagePaths),
            };

            _logger.LogInformation("Sub-goal {Id} executing with LoopAgent (max {Max} iterations): {Description}",
                goal.Id, MaxAttemptsPerSubGoal, goal.Description);

            await foreach (var update in loopAgent.RunStreamingAsync(
                messages, cancellationToken: ct).ConfigureAwait(false))
            {
                // Agent text remains in the MAF response and evaluation evidence.
                // The Goal transcript intentionally exposes only projected progress,
                // tool activity and file changes rather than every model token.
                _ = update;
            }

            // 兜底：如果 evaluator 未被触发（异常路径），至少记为 1 次
            if (actualIterations == 0)
                actualIterations = 1;

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
                Evaluation: completed ? "Hard validation and semantic acceptance passed" : "Exhausted retry attempts",
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

    /// <summary>
    /// 使用 LLM-as-judge 评估子目标的语义覆盖。
    /// 解析 "VERDICT: DONE" / "VERDICT: MORE" 标记，提取反馈供下一轮重试使用。
    /// 无 marker 时视为未完成（MORE wins），使用整个响应作为反馈。
    /// </summary>
    private async Task<(bool Completed, string? Feedback, long InputTokens, long OutputTokens)> EvaluateSubGoalAsync(
        SubGoalEvidence evidence,
        GoalItem goal,
        CancellationToken ct)
    {
        var criteria = string.IsNullOrWhiteSpace(goal.SuccessCriteria)
            ? "Complete the user's original request as fully as possible."
            : goal.SuccessCriteria;

        var judgePrompt = $"""
            You are a judge evaluating whether a sub-goal has been completed.

            Sub-goal: {goal.Description}
            Success criteria: {criteria}

            Verified execution evidence:
            {FormatEvidenceForJudge(evidence)}

            Deterministic gates have passed. Evaluate only semantic coverage of the sub-goal and success criteria.
            Reply with exactly one of:
            - "VERDICT: DONE" if the criteria are met
            - "VERDICT: MORE" followed by specific, actionable feedback on what is still missing or incomplete

            Be strict but fair. Only say DONE if there is concrete evidence of completion.
            """;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a strict but fair judge evaluating task completion. Reply with VERDICT: DONE or VERDICT: MORE."),
            new(ChatRole.User, judgePrompt),
        };

        var chatOptions = new ChatOptions { MaxOutputTokens = 1024 };
        var response = await _chatClient.GetResponseAsync(messages, chatOptions, ct).ConfigureAwait(false);
        var verdict = response.Text ?? "";

        var inputTokens = (long)(response.Usage?.InputTokenCount ?? 0);
        var outputTokens = (long)(response.Usage?.OutputTokenCount ?? 0);

        if (verdict.Contains("VERDICT: DONE", StringComparison.OrdinalIgnoreCase))
            return (true, null, inputTokens, outputTokens);

        // 提取 "VERDICT: MORE" 之后的反馈内容；无 marker 时使用整个响应作为反馈
        var moreIdx = verdict.IndexOf("VERDICT: MORE", StringComparison.OrdinalIgnoreCase);
        var feedback = moreIdx >= 0
            ? verdict[(moreIdx + "VERDICT: MORE".Length)..].Trim()
            : verdict.Trim();

        return (false, feedback, inputTokens, outputTokens);
    }

    public async Task<(bool Passed, string Summary, long InputTokens, long OutputTokens)> EvaluateFinalGoalAsync(
        string originalGoal,
        IReadOnlyList<GoalItem> goals,
        IReadOnlyList<SubGoalExecution> executions,
        CancellationToken ct)
    {
        var evidenceSummary = new StringBuilder();
        foreach (var goal in goals.Where(goal => !goal.Optional))
        {
            var execution = executions.LastOrDefault(item => item.GoalId == goal.Id);
            evidenceSummary.AppendLine(CultureInfo.InvariantCulture, $"Sub-goal #{goal.Id}: {goal.Description}");
            evidenceSummary.AppendLine(CultureInfo.InvariantCulture, $"Success criteria: {goal.SuccessCriteria}");
            evidenceSummary.AppendLine(CultureInfo.InvariantCulture, $"Status: {execution?.Status.ToString() ?? "Missing"}");
            if (execution?.Evidence is { } evidence)
                evidenceSummary.AppendLine(FormatEvidenceForJudge(evidence));
        }

        var prompt = $"""
            You are the final semantic reviewer for a completed engineering goal.

            Original goal:
            {originalGoal}

            Required sub-goal evidence:
            {evidenceSummary}

            All deterministic gates have passed. Decide whether the combined evidence covers the original goal
            across sub-goal boundaries without material omissions or contradictions.
            Reply with exactly one of:
            - "VERDICT: DONE" if the original goal is fully covered
            - "VERDICT: MORE" followed by the specific missing integration or requirement coverage
            """;
        var response = await _chatClient.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, "You are a strict final goal reviewer. Reply with VERDICT: DONE or VERDICT: MORE."),
                new ChatMessage(ChatRole.User, prompt),
            ],
            new ChatOptions { MaxOutputTokens = 1024 },
            ct).ConfigureAwait(false);
        var verdict = response.Text ?? string.Empty;
        var passed = verdict.Contains("VERDICT: DONE", StringComparison.OrdinalIgnoreCase);
        return (
            passed,
            passed ? "Final AI semantic review returned DONE." : verdict.Trim(),
            (long)(response.Usage?.InputTokenCount ?? 0),
            (long)(response.Usage?.OutputTokenCount ?? 0));
    }

    private async Task<SubGoalHardValidationResult> ValidateSubGoalAsync(
        GoalItem goal,
        string workingDirectory,
        IReadOnlyList<string> changedFiles,
        IReadOnlyList<GoalToolExecutionEvidence> toolExecutions,
        string agentOutput,
        CancellationToken ct)
    {
        var validations = new List<GoalValidationEvidence>();
        var fullWorkingDirectory = Path.GetFullPath(workingDirectory);
        var relativeChangedFiles = changedFiles
            .Select(path => Path.GetRelativePath(fullWorkingDirectory, Path.GetFullPath(path)))
            .ToList();

        var toolErrors = toolExecutions.Where(e => e.IsError).ToList();
        validations.Add(new GoalValidationEvidence(
            "tool-execution",
            toolErrors.Count == 0,
            false,
            toolErrors.Count == 0
                ? $"{toolExecutions.Count} tool execution(s) completed without unresolved errors."
                : $"{toolErrors.Count} tool execution error(s): {string.Join("; ", toolErrors.Select(e => $"{e.ToolName}: {e.Result}"))}"));

        var missingFiles = goal.ExpectedFiles
            .Select(path => ResolveWorkspacePath(fullWorkingDirectory, path))
            .Where(path => !File.Exists(path) && !Directory.Exists(path))
            .Select(path => Path.GetRelativePath(fullWorkingDirectory, path))
            .ToList();
        validations.Add(new GoalValidationEvidence(
            "expected-artifacts",
            missingFiles.Count == 0,
            goal.ExpectedFiles.Count == 0,
            goal.ExpectedFiles.Count == 0
                ? "No explicit expected artifacts were declared."
                : missingFiles.Count == 0
                    ? $"All {goal.ExpectedFiles.Count} expected artifact(s) exist."
                    : $"Missing expected artifact(s): {string.Join(", ", missingFiles)}"));

        var outOfScope = FindOutOfScopeFiles(fullWorkingDirectory, changedFiles, goal.AllowedPaths);
        validations.Add(new GoalValidationEvidence(
            "change-scope",
            outOfScope.Count == 0,
            false,
            outOfScope.Count == 0
                ? $"All {changedFiles.Count} changed file(s) are inside the allowed workspace scope."
                : $"Out-of-scope file modification(s): {string.Join(", ", outOfScope)}"));

        var sourceFilesChanged = _verificationProvider is not null
            && changedFiles.Any(_verificationProvider.IsSourceFile);
        var verificationRequired = goal.RequiresBuild || goal.RequiresTests || sourceFilesChanged;
        VerificationResult? verification = null;
        if (verificationRequired)
        {
            verification = _verificationProvider is null
                ? null
                : goal.RequiresTests
                    ? await _verificationProvider.VerifyBuildAndTestsAsync(workingDirectory, changedFiles, ct).ConfigureAwait(false)
                    : await _verificationProvider.VerifyAsync(workingDirectory, changedFiles, ct).ConfigureAwait(false);
            validations.Add(new GoalValidationEvidence(
                goal.RequiresTests ? "build-and-test" : "build",
                verification is { Success: true, Skipped: false },
                false,
                verification?.FormatForLlm() ?? "No verification provider is registered."));
        }
        else
        {
            validations.Add(new GoalValidationEvidence(
                "build",
                true,
                true,
                "No source changes or explicit build/test requirement were detected."));
        }

        var diagnostics = GetRelevantDiagnostics(fullWorkingDirectory, changedFiles);
        validations.Add(new GoalValidationEvidence(
            "static-diagnostics",
            diagnostics.Count == 0,
            _diagnosticRegistry is null,
            _diagnosticRegistry is null
                ? "LSP diagnostic registry is unavailable; compiler verification remains authoritative."
                : diagnostics.Count == 0
                    ? "No unresolved LSP errors were reported for changed files."
                    : $"Unresolved LSP error(s): {string.Join("; ", diagnostics)}"));

        var evidence = new SubGoalEvidence(
            AgentSummary: agentOutput,
            ChangedFiles: relativeChangedFiles,
            ToolExecutions: toolExecutions.ToList(),
            Validations: validations,
            Diagnostics: diagnostics);
        var failedGates = validations.Where(v => !v.Passed && !v.Skipped).ToList();
        return failedGates.Count == 0
            ? new SubGoalHardValidationResult(true, null, evidence)
            : new SubGoalHardValidationResult(
                false,
                "Deterministic validation failed. Fix these issues before claiming completion:\n" +
                string.Join("\n", failedGates.Select(v => $"- {v.Gate}: {v.Summary}")),
                evidence);
    }

    private IReadOnlyList<string> GetRelevantDiagnostics(
        string workingDirectory,
        IReadOnlyList<string> changedFiles)
    {
        if (_diagnosticRegistry is null || changedFiles.Count == 0)
            return Array.Empty<string>();

        var changed = changedFiles
            .Select(Path.GetFullPath)
            .ToHashSet(Core.IO.PathComparer.Default);
        return _diagnosticRegistry.GetAllDiagnostics()
            .Where(d => d.Severity == LspDiagnosticSeverity.Error)
            .Where(d => PathBoundary.IsWithinDirectory(d.FilePath, workingDirectory))
            .Where(d => changed.Contains(Path.GetFullPath(d.FilePath)))
            .Select(d => d.Summary)
            .ToList();
    }

    private static IReadOnlyList<string> FindOutOfScopeFiles(
        string workingDirectory,
        IReadOnlyList<string> changedFiles,
        IReadOnlyList<string> allowedPaths)
    {
        var allowedRoots = allowedPaths
            .Select(path => ResolveWorkspacePath(workingDirectory, path))
            .ToList();
        return changedFiles
            .Select(Path.GetFullPath)
            .Where(path => !PathBoundary.IsWithinDirectory(path, workingDirectory)
                || allowedRoots.Count > 0 && !allowedRoots.Any(root => PathBoundary.IsWithinDirectory(path, root)))
            .Select(path => Path.GetRelativePath(workingDirectory, path))
            .ToList();
    }

    private static string ResolveWorkspacePath(string workingDirectory, string path)
    {
        var resolved = Path.GetFullPath(path, workingDirectory);
        if (!PathBoundary.IsWithinDirectory(resolved, workingDirectory))
            throw new InvalidOperationException($"Declared Goal path '{path}' is outside the working directory.");
        return resolved;
    }

    private static string FormatEvidenceForJudge(SubGoalEvidence evidence)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Agent summary:");
        builder.AppendLine(evidence.AgentSummary);
        builder.AppendLine(CultureInfo.InvariantCulture, $"Changed files: {(evidence.ChangedFiles.Count == 0 ? "(none)" : string.Join(", ", evidence.ChangedFiles))}");
        builder.AppendLine("Deterministic validation:");
        foreach (var validation in evidence.Validations)
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {validation.Gate}: {(validation.Skipped ? "SKIPPED" : validation.Passed ? "PASSED" : "FAILED")} — {validation.Summary}");
        return builder.ToString();
    }

    private static void CaptureToolEvidence(
        OrchestrationEvent evt,
        ICollection<GoalToolExecutionEvidence> toolExecutions)
    {
        if (evt is OrchestrationEvent.ToolDone done)
            toolExecutions.Add(new GoalToolExecutionEvidence(done.Name, done.IsError, done.Result));
    }

    private sealed record SubGoalHardValidationResult(
        bool Passed,
        string? Feedback,
        SubGoalEvidence Evidence);

    /// <summary>
    /// 更新 GOAL 模式共享上下文（供子目标执行期间 system prompt 注入使用）。
    /// </summary>
    public void UpdateGoalContext(GoalPlan plan, GoalItem currentGoal, IReadOnlyList<SubGoalExecution> executions, bool sharedTransactionOwned)
    {
        var completedSummaries = executions
            .Where(e => e.Status == GoalStatus.Completed)
            .Select(e => (e.GoalId, plan.Goals.FirstOrDefault(g => g.Id == e.GoalId)?.Description ?? "", TruncateForSummary(e.AgentOutput)))
            .ToList();

        var failedSummaries = executions
            .Where(e => e.Status == GoalStatus.Failed)
            .Select(e => (e.GoalId, plan.Goals.FirstOrDefault(g => g.Id == e.GoalId)?.Description ?? "", e.Evaluation))
            .ToList();

        var snapshot = new GoalContextSnapshot(
            CurrentGoalId: currentGoal.Id,
            TotalGoals: plan.Goals.Count,
            CompletedSummaries: completedSummaries,
            FailedSummaries: failedSummaries,
            SharedTransactionHint: true,
            CurrentGoalDepth: currentGoal.Depth);

        _goalContextState.Update(snapshot);
    }

    /// <summary>
    /// 生成 GOAL 模式执行汇总文本。
    /// </summary>
    public static string BuildSummary(
        GoalPlan plan,
        IReadOnlyList<SubGoalExecution> executions,
        long totalInputTokens,
        long totalOutputTokens,
        bool usedFallback)
    {
        var completed = plan.Goals.Where(g => g.Status == GoalStatus.Completed).ToList();
        var failed = plan.Goals.Where(g => g.Status == GoalStatus.Failed).ToList();
        var skipped = plan.Goals.Where(g => g.Status == GoalStatus.Skipped).ToList();

        var lines = new List<string>
        {
            "",
            "═══════════════════════════════════════",
            "  GOAL MODE EXECUTION SUMMARY",
            "═══════════════════════════════════════",
            "",
        };

        if (usedFallback)
            lines.Add("  (Decomposition failed — executed as single goal)");

        lines.Add($"  Completed: {completed.Count}/{plan.Goals.Count}");
        lines.Add($"  Failed:    {failed.Count}/{plan.Goals.Count}");

        if (skipped.Count > 0)
            lines.Add($"  Skipped:   {skipped.Count}/{plan.Goals.Count}");

        lines.Add("");
        lines.Add($"  Total attempts:   {executions.Sum(e => e.Attempts)}");
        lines.Add($"  Input tokens:     {totalInputTokens:N0}");
        lines.Add($"  Output tokens:    {totalOutputTokens:N0}");

        if (executions.Count > 1)
        {
            lines.Add("");
            lines.Add("  Per-sub-goal token usage:");
            foreach (var exec in executions)
            {
                var goalDesc = plan.Goals.FirstOrDefault(g => g.Id == exec.GoalId)?.Description ?? "(unknown)";
                if (goalDesc.Length > 40) goalDesc = goalDesc[..37] + "...";
                lines.Add($"    #{exec.GoalId} ({exec.Status}, {exec.Attempts} attempts): {exec.InputTokens + exec.OutputTokens:N0} tokens — {goalDesc}");
            }
        }

        if (completed.Count > 0)
        {
            lines.Add("");
            lines.Add("  Completed sub-goals:");
            foreach (var g in completed)
                lines.Add($"    ✓ #{g.Id}: {g.Description}");
        }

        if (failed.Count > 0)
        {
            lines.Add("");
            lines.Add("  Failed sub-goals:");
            foreach (var g in failed)
            {
                var exec = executions.FirstOrDefault(e => e.GoalId == g.Id);
                lines.Add($"    ✗ #{g.Id}: {g.Description}");
                if (exec is not null && !string.IsNullOrEmpty(exec.Evaluation))
                    lines.Add($"      → {exec.Evaluation}");
            }
        }

        if (skipped.Count > 0)
        {
            lines.Add("");
            lines.Add("  Skipped sub-goals (due to iteration limit or prior failures):");
            foreach (var g in skipped)
                lines.Add($"    - #{g.Id}: {g.Description}");
        }

        lines.Add("");
        lines.Add("═══════════════════════════════════════");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// 构造子目标的 <see cref="MainAgentRunOptions"/>。
    /// </summary>
    private async Task<MainAgentRunOptions> BuildSubGoalRunOptionsAsync(
        GoalItem goal,
        GoalRunOptions options,
        EditTransaction sharedTransaction,
        string userPrompt,
        Action<OrchestrationEvent> evidenceSink,
        CancellationToken ct)
    {
        // Load sub-goal execution system prompt from IPromptManager (three-layer store:
        // project > user > built-in). Throws if missing — prompts/system/goal-subgoal.prompt
        // is always shipped via csproj Content copy, so this should never fail in production.
        var systemPrompt = await _promptManager.GetPromptAsync("system/goal-subgoal", ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Mode prompt 'system/goal-subgoal' not found in any IPromptManager store.");

        // 按子目标 requiredTools 裁剪工具集。
        // null/空/"*" → 全量工具集；否则按名称过滤。
        var filteredTools = FilterToolsForSubGoal(options.Tools, goal.RequiredTools);

        return new MainAgentRunOptions
        {
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            ModelId = options.ModelId,
            WorkingDirectory = options.WorkingDirectory,
            MaxTurns = options.MaxTurnsPerSubGoal,
            Tools = filteredTools,
            WorkingMode = WorkingMode.Goal,
            SharedTransaction = sharedTransaction,
            // 同一事件同时进入 UI 和确定性验证证据收集器。
            OrchestrationEventSink = evt =>
            {
                evidenceSink(evt);
                options.OrchestrationEventSink?.Invoke(evt);
            },
        };
    }

    /// <summary>
    /// 按子目标 RequiredTools 白名单过滤工具集。
    /// 规则：
    /// - RequiredTools 为 null/空 → 返回全量工具集（不裁剪）
    /// - RequiredTools 包含 "*" → 返回全量工具集（显式通配）
    /// - 否则 → 仅保留名称匹配的工具
    /// - 过滤后为空 → 抛出异常（fail-closed），不回退到全量工具集
    /// </summary>
    private static IList<AITool> FilterToolsForSubGoal(
        IList<AITool> allTools,
        IReadOnlyList<string>? requiredTools)
    {
        if (requiredTools is null || requiredTools.Count == 0)
            return allTools;

        if (requiredTools.Contains("*"))
            return allTools;

        var allowedSet = new HashSet<string>(requiredTools, StringComparer.Ordinal);
        var filtered = allTools
            .Where(t => t is AIFunction af && allowedSet.Contains(af.Name))
            .ToList();

        // P2-1: fail-closed — do not fall back to allTools when no match is found.
        // Unknown tool names in the plan indicate a decomposition error; using allTools
        // would silently expand permissions beyond what the plan intended.
        if (filtered.Count == 0)
        {
            throw new InvalidOperationException(
                $"No valid tools found for requiredTools [{string.Join(", ", requiredTools)}]. " +
                "The goal decomposition produced unknown tool names. " +
                "Fix the plan or use '*' to explicitly allow all tools.");
        }

        return filtered;
    }

    /// <summary>
    /// 生成子目标的初始 user prompt。
    /// 反馈注入由 <see cref="LoopAgent"/> 通过 <c>FeedbackMessageTemplate</c> 自动处理，
    /// 无需在此方法中拼接 feedback。
    /// </summary>
    private Task<string> BuildSubGoalPromptAsync(GoalItem goal, CancellationToken ct)
    {
        var prompt =
            $"""
            ## Sub-goal {goal.Id}: {goal.Description}

            Success criteria: {goal.SuccessCriteria}

            Execute this sub-goal. When done, clearly state whether the success criteria
            were met, and provide specific evidence (e.g., test results, file changes,
            command output) to support your conclusion.
            """;

        return Task.FromResult(prompt);
    }

    private static string TruncateForSummary(string output, int maxChars = 500)
    {
        if (string.IsNullOrEmpty(output)) return "";
        if (output.Length <= maxChars) return output;
        return output[..maxChars] + "...";
    }

    /// <summary>
    /// Builds a user ChatMessage with optional image attachments as DataContent blocks.
    /// </summary>
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
