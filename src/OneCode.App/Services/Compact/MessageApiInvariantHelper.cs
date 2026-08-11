using Microsoft.Extensions.AI;

namespace OneCode.App.Services.Compact;

public static partial class MessageApiInvariantHelper
{
    public static int AdjustIndexToPreserveApiInvariants(IReadOnlyList<Message> messages, int startIndex)
        => AdjustRangeToAtomicBoundaries(messages, startIndex, messages.Count).FromIndex;

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

    public static IEnumerable<string> GetToolResultIds(Message message)
    {
        if (message is ToolResultMessage trm)
        {
            return new[] { trm.ToolUseId };
        }
        return Enumerable.Empty<string>();
    }

    public static bool IsToolResultMessage(ChatMessage msg)
    {
        return msg.Role == ChatRole.User
            && msg.Contents.OfType<FunctionResultContent>().Any();
    }

    public static string GetToolResultCallId(ChatMessage msg)
    {
        var fr = msg.Contents.OfType<FunctionResultContent>().FirstOrDefault();
        if (fr is not null && !string.IsNullOrEmpty(fr.CallId))
            return fr.CallId;

        if (msg.AdditionalProperties is { } props
            && props.TryGetValue("tool_call_id", out var id)
            && id is string callId)
            return callId;

        return string.Empty;
    }

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

    /// <summary>
    /// Validates that tool_use → tool_result pairings are consistent across the message list.
    /// Returns a list of orphaned tool_result IDs (referencing non-existent tool_use blocks)
    /// that would cause HTTP 400 from the Anthropic API.
    /// </summary>
    public static IReadOnlyList<string> FindOrphanedToolResults(IReadOnlyList<ChatMessage> messages)
    {
        List<string> orphans = [];
        var availableToolUseIds = new HashSet<string>();

        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];

            if (msg.Role == ChatRole.Assistant)
            {
                foreach (var fc in msg.Contents.OfType<FunctionCallContent>())
                {
                    if (!string.IsNullOrEmpty(fc.CallId))
                        availableToolUseIds.Add(fc.CallId);
                }
            }

            if (msg.Role == ChatRole.User)
            {
                foreach (var fr in msg.Contents.OfType<FunctionResultContent>())
                {
                    if (!string.IsNullOrEmpty(fr.CallId) && !availableToolUseIds.Contains(fr.CallId))
                        orphans.Add(fr.CallId);
                }
            }
        }

        return orphans;
    }

    public static IReadOnlyList<string> FindOrphanedToolUses(IReadOnlyList<ChatMessage> messages)
    {
        List<string> orphans = [];
        var toolUseIds = new HashSet<string>();
        var toolResultIds = new HashSet<string>();

        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.Assistant)
            {
                foreach (var fc in msg.Contents.OfType<FunctionCallContent>())
                {
                    if (!string.IsNullOrEmpty(fc.CallId))
                        toolUseIds.Add(fc.CallId);
                }
            }

            if (msg.Role == ChatRole.User)
            {
                foreach (var fr in msg.Contents.OfType<FunctionResultContent>())
                {
                    if (!string.IsNullOrEmpty(fr.CallId))
                        toolResultIds.Add(fr.CallId);
                }
            }
        }

        foreach (var id in toolUseIds)
        {
            if (!toolResultIds.Contains(id))
                orphans.Add(id);
        }

        return orphans;
    }

    public static IReadOnlyList<ChatMessage> RemoveOrphanedToolUses(
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<string> orphanedIds)
    {
        if (orphanedIds.Count == 0)
            return messages;

        var orphanSet = new HashSet<string>(orphanedIds, StringComparer.Ordinal);
        var result = new List<ChatMessage>(messages.Count);

        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.Assistant)
            {
                var hasOrphan = msg.Contents
                    .OfType<FunctionCallContent>()
                    .Any(fc => orphanSet.Contains(fc.CallId));

                if (hasOrphan)
                {
                    var filtered = msg.Contents.Where(c =>
                        c is not FunctionCallContent fc || !orphanSet.Contains(fc.CallId)).ToList();

                    if (filtered.Count == 0)
                        continue;

                    result.Add(new ChatMessage(msg.Role, filtered)
                    {
                        AdditionalProperties = msg.AdditionalProperties,
                        AuthorName = msg.AuthorName,
                    });
                    continue;
                }
            }

            result.Add(msg);
        }

        return result;
    }
}
