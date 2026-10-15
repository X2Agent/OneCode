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

    public static PlanHandoff Extract(IReadOnlyList<ChatMessage> history, int windowStart = 0)
    {
        var userIndices = new List<int>();
        for (var index = Math.Max(windowStart, 0); index < history.Count; index++)
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

        for (var index = first; index < history.Count; index++)
        {
            var message = history[index];
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

            if (index != first && selected.Contains(index))
                clarifications.Add($"User follow-up: {Flatten(message.Text)}");
        }

        return new PlanHandoff(Truncate(history[first].Text.Trim()), clarifications);
    }

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
