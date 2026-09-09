using Microsoft.Extensions.AI;

namespace OneCode.App.Services.Compact;

public static partial class MessageApiInvariantHelper
{
    /// <summary>
    /// Expands a half-open range so neither boundary cuts through an assistant tool-call
    /// message and its complete ordered result set. Non-tool messages are single-message groups.
    /// </summary>
    public static (int FromIndex, int UpToIndex) AdjustRangeToAtomicBoundaries(
        IReadOnlyList<Message> messages,
        int fromIndex,
        int upToIndex)
    {
        var from = Math.Clamp(fromIndex, 0, messages.Count);
        var to = Math.Clamp(upToIndex, from, messages.Count);
        foreach (var group in GetAtomicGroups(messages))
        {
            if (from > group.Start && from < group.End)
                from = group.Start;
            if (to > group.Start && to < group.End)
                to = group.End;
        }
        return (from, to);
    }

    /// <summary>Returns ordered half-open message groups used as legal compaction boundaries.</summary>
    public static IReadOnlyList<(int Start, int End)> GetAtomicGroups(IReadOnlyList<Message> messages)
    {
        List<(int Start, int End)> groups = [];
        for (var index = 0; index < messages.Count;)
        {
            if (messages[index] is AssistantMessage assistant)
            {
                var callIds = assistant.Content
                    .OfType<ToolUseBlock>()
                    .Select(block => block.Id)
                    .ToHashSet(StringComparer.Ordinal);
                if (callIds.Count > 0)
                {
                    var end = index + 1;
                    while (end < messages.Count && messages[end] is ToolResultMessage)
                        end++;

                    // Even malformed legacy batches stay indivisible during compaction;
                    // replay migration decides whether the complete group is usable.
                    groups.Add((index, end));
                    index = end;
                    continue;
                }
            }

            groups.Add((index, index + 1));
            index++;
        }
        return groups;
    }

    // 仅单元测试使用：生产代码当前无调用方（测试接缝）。
    public static IReadOnlyList<ChatMessage> NormalizeForToolCallingTransport(IEnumerable<ChatMessage> messages)
    {
        List<ChatMessage> normalized = [];

        foreach (var message in messages)
        {
            var hasFunctionCall = message.Contents.OfType<FunctionCallContent>().Any();
            var hasFunctionResult = message.Contents.OfType<FunctionResultContent>().Any();
            var shouldStripEmptyText = (hasFunctionCall || hasFunctionResult)
                && string.IsNullOrEmpty(message.Text);

            if (!shouldStripEmptyText)
            {
                normalized.Add(message);
                continue;
            }

            var filteredContents = message.Contents
                .Where(static content => content is not TextContent { Text.Length: 0 })
                .ToList();

            if (filteredContents.Count == 0)
                continue;

            var clone = new ChatMessage(message.Role, filteredContents)
            {
                AdditionalProperties = message.AdditionalProperties,
                AuthorName = message.AuthorName,
                CreatedAt = message.CreatedAt,
                MessageId = message.MessageId,
                RawRepresentation = message.RawRepresentation,
            };

            normalized.Add(clone);
        }

        return normalized;
    }

}
