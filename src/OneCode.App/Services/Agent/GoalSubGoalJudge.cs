using System.Text;
using Microsoft.Extensions.AI;
using OneCode.Core.Prompt;

namespace OneCode.App.Services.Agent;

/// <summary>
/// W4-B: semantic sub-goal / final-goal judge used inside DelegateLoopEvaluator.
/// Kept custom (not AIJudgeLoopEvaluator) — see plan §2.6 / §14.
/// </summary>
internal sealed class GoalSubGoalJudge(
    IChatClient chatClient,
    IPromptManager promptManager)
{
    private readonly IChatClient _chatClient = chatClient;
    private readonly IPromptManager _promptManager = promptManager;

    /// <summary>
    /// 使用 LLM-as-judge 评估子目标的语义覆盖。
    /// 解析 "VERDICT: DONE" / "VERDICT: MORE" 标记，提取反馈供下一轮重试使用。
    /// 无 marker 时视为未完成（MORE wins），使用整个响应作为反馈。
    /// </summary>
    public async Task<(bool Completed, string? Feedback, long InputTokens, long OutputTokens)> EvaluateSubGoalAsync(
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
            {GoalSubGoalAssessment.FormatEvidenceForJudge(evidence)}

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
                evidenceSummary.AppendLine(GoalSubGoalAssessment.FormatEvidenceForJudge(evidence));
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
}
