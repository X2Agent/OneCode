using Microsoft.Extensions.AI;

namespace OneCode.App.Services.PlanMode;

/// <summary>规划阶段需要带进执行轮的用户侧信息：原始诉求 + 澄清问答/后续反馈。</summary>
public sealed record PlanHandoff(string OriginalRequest, IReadOnlyList<string> Clarifications);

/// <summary>
/// 从规划阶段转录里提取 PLAN→BUILD 交接信息。计划正文只写「做什么」，
/// 用户的原话、澄清答案与修订反馈显式冻结进审批快照，确保跨上下文压缩或进程恢复时硬约束不丢失。
/// </summary>
/// <remarks>
/// 只取用户侧信息（用户消息 + AskUserQuestion 的问答结果），不带 assistant 的调研过程——
/// 后者已在会话历史与计划正文中承载。
/// </remarks>
public static class PlanHandoffExtractor
{
    private const string AskUserQuestionToolName = "AskUserQuestion";

    // 工具失败/取消的结果文案都以工具名开头，不是用户答案。
    private const string AskUserQuestionFailurePrefix = "AskUserQuestion ";

    // 最多带 5 条用户消息：原始诉求 + 最近 4 条追问/修订反馈；中间过多的往返不进交接上下文。
    private const int MaxUserMessages = 5;

    // 单条文本上限：防止用户粘贴大段日志把交接上下文撑爆（每轮都会重复注入）。
    private const int MaxEntryChars = 2_000;

    /// <summary>执行轮首条触发消息的前缀，不属于用户意图。</summary>
    private const string TriggerPrefix = "Execute approved plan ";

    public static PlanHandoff Extract(
        IReadOnlyList<ChatMessage> history,
        int windowStart = 0,
        string? currentPlanId = null)
    {
        var effectiveStart = ResolveEffectiveWindowStart(history, Math.Max(windowStart, 0), currentPlanId);
        var userIndices = new List<int>();
        for (var index = effectiveStart; index < history.Count; index++)
        {
            if (IsUserIntent(history[index]))
                userIndices.Add(index);
        }

        if (userIndices.Count == 0)
            return new PlanHandoff(string.Empty, []);

        // 原始诉求恒取窗口内第一条；其后只保留最近的追问，避免长对话把原话挤出去。
        var first = userIndices[0];
        var selected = userIndices.Skip(1).TakeLast(MaxUserMessages - 1).Prepend(first).ToArray();
        var askCallIds = new HashSet<string>(StringComparer.Ordinal);
        var clarifications = new List<string>();
        var inExecutionTurn = false;

        for (var index = first; index < history.Count; index++)
        {
            var message = history[index];
            if (IsExecutionTrigger(message))
            {
                inExecutionTurn = true;
                continue;
            }

            if (IsUserIntent(message))
                inExecutionTurn = false;

            if (!inExecutionTurn)
            {
                foreach (var content in message.Contents)
                {
                    switch (content)
                    {
                        case FunctionCallContent call when call.Name == AskUserQuestionToolName:
                            askCallIds.Add(call.CallId);
                            break;
                        case FunctionResultContent result when askCallIds.Contains(result.CallId):
                            if (ReadResultText(result) is { Length: > 0 } answer
                                && !answer.StartsWith(AskUserQuestionFailurePrefix, StringComparison.Ordinal))
                                clarifications.Add($"Q&A: {Flatten(answer)}");
                            break;
                    }
                }
            }

            if (index != first && selected.Contains(index))
                clarifications.Add($"User follow-up: {Flatten(message.Text)}");
        }

        return new PlanHandoff(Truncate(history[first].Text.Trim()), clarifications);
    }

    /// <summary>
    /// 若窗口内存在旧计划的受控执行触发消息（<c>Execute approved plan ...</c>）且其后又有新的用户意图，
    /// 则从最后一条旧触发消息之后开始提取，避免把上一轮已执行计划的诉求与问答混入新计划。
    /// 属于同一 <paramref name="currentPlanId"/> 的触发消息（同计划失败/修订后重审）不截断起点，以保留最初诉求。
    /// </summary>
    private static int ResolveEffectiveWindowStart(
        IReadOnlyList<ChatMessage> history,
        int start,
        string? currentPlanId)
    {
        var effectiveStart = start;
        for (var index = start; index < history.Count; index++)
        {
            if (!IsExecutionTrigger(history[index]) || IsTriggerForPlan(history[index], currentPlanId))
                continue;

            for (var next = index + 1; next < history.Count; next++)
            {
                if (IsUserIntent(history[next]))
                {
                    effectiveStart = next;
                    break;
                }
            }
        }

        return effectiveStart;
    }

    private static bool IsExecutionTrigger(ChatMessage message)
        => message.Role == ChatRole.User
            && !string.IsNullOrWhiteSpace(message.Text)
            && message.Text.StartsWith(TriggerPrefix, StringComparison.Ordinal);

    private static bool IsTriggerForPlan(ChatMessage message, string? planId)
        => !string.IsNullOrWhiteSpace(planId)
            && message.Text.StartsWith($"{TriggerPrefix}{planId} ", StringComparison.Ordinal);

    private static bool IsUserIntent(ChatMessage message)
        => message.Role == ChatRole.User
            && message.Contents.All(content => content is TextContent)
            && !string.IsNullOrWhiteSpace(message.Text)
            && !message.Text.StartsWith(TriggerPrefix, StringComparison.Ordinal);

    private static string? ReadResultText(FunctionResultContent result) => result.Result switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        null => null,
        var other => other.ToString(),
    };

    private static string Flatten(string text)
        => Truncate(string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));

    private static string Truncate(string text)
        => text.Length <= MaxEntryChars ? text : $"{text[..MaxEntryChars]}…";
}
