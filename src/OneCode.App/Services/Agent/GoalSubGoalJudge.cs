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
    /// <summary>完成标记。与 <see cref="MoreMarker"/> 互不为子串，歧义判定可二分。</summary>
    internal const string DoneMarker = "VERDICT: DONE";

    /// <summary>未完成标记。歧义时优先于 <see cref="DoneMarker"/>，与 MAF <c>AIJudgeLoopEvaluator</c> 一致。</summary>
    internal const string MoreMarker = "VERDICT: MORE";

    private readonly IChatClient _chatClient = chatClient;
    private readonly IPromptManager _promptManager = promptManager;

    /// <summary>
    /// 使用 LLM-as-judge 评估子目标的语义覆盖。
    /// 解析 "VERDICT: DONE" / "VERDICT: MORE" 标记，提取反馈供下一轮重试使用。
    /// 无 marker 时视为未完成（MORE wins），使用整个响应作为反馈。
    /// </summary>
    /// <remarks>
    /// 判定规则与 MAF <c>AIJudgeLoopEvaluator</c> 对齐：<c>!MORE &amp;&amp; DONE</c>，
    /// 即判定歧义（双标记并存 / 无标记 / 格式漂移）时 MORE 胜出、循环继续。
    /// 反向规则（见到 DONE 即判完成）会把未完成的子目标静默放行为已完成。
    /// </remarks>
    public async Task<(bool Completed, string? Feedback, long InputTokens, long OutputTokens)> EvaluateSubGoalAsync(
        SubGoalEvidence evidence,
        GoalItem goal,
        CancellationToken ct,
        string? workingDirectory = null)
    {
        var criteria = string.IsNullOrWhiteSpace(goal.SuccessCriteria)
            ? "Complete the user's original request as fully as possible."
            : goal.SuccessCriteria;

        var judgePrompt = $"""
            You are a judge evaluating whether a sub-goal has been completed.

            Sub-goal: {goal.Description}
            Success criteria: {criteria}

            Verified execution evidence:
            {GoalSubGoalAssessment.FormatEvidenceForJudge(evidence, workingDirectory)}

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

        // 默认输出上限与 Length 截断恢复统一由底层客户端管道（MaxOutputTokensDecorator）处理。
        var response = await _chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
        var verdict = response.Text ?? "";

        var inputTokens = (long)(response.Usage?.InputTokenCount ?? 0);
        var outputTokens = (long)(response.Usage?.OutputTokenCount ?? 0);

        // MORE 优先：判定歧义时保持循环，而不是放行未完成的子目标。
        var moreIdx = verdict.IndexOf(MoreMarker, StringComparison.OrdinalIgnoreCase);
        if (moreIdx >= 0)
        {
            // 提取 "VERDICT: MORE" 之后的反馈内容。
            var moreFeedback = verdict[(moreIdx + MoreMarker.Length)..].Trim();
            return (false, string.IsNullOrWhiteSpace(moreFeedback) ? verdict.Trim() : moreFeedback, inputTokens, outputTokens);
        }

        // 无 MORE 且含 DONE 才判完成；无标记时视为未完成，整体响应作为反馈。
        return verdict.Contains(DoneMarker, StringComparison.OrdinalIgnoreCase)
            ? (true, null, inputTokens, outputTokens)
            : (false, verdict.Trim(), inputTokens, outputTokens);
    }
}
