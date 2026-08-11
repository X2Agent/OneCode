namespace OneCode.App.Services.Compact;

/// <summary>
/// Applies the result of a compact run to the <see cref="Conversation"/>:
/// replaces the message history with the boundary marker + summary, restores the
/// protected tail, and trims duplicate system markers left over from previous compactions.
///
/// Extracted from <see cref="CompactService"/> so message-mutation concerns are
/// isolated from orchestration and prompt construction.
/// </summary>
public sealed class CompactApplier
{
    /// <summary>
    /// Full compact: replace the entire message history with the boundary marker,
    /// the summary, and a verbatim copy of the most
    /// recent <see cref="CompactConstants.RecentMessagesToKeep"/> messages.
    /// </summary>
    public void ApplyFullCompact(Conversation session, string formattedSummary)
    {
        var retainedMessages = NormalizeToolPairs(SelectRetainedMessages(session.Messages));
        session.Messages.Clear();

        session.Messages.Add(new SystemMessage(
            Id: Guid.NewGuid().ToString("N"),
            Content: CompactConstants.CompactBoundaryContent,
            Timestamp: DateTimeOffset.UtcNow,
            Type: SystemMessageType.Info));

        session.Messages.Add(new UserMessage(
            Id: Guid.NewGuid().ToString("N"),
            Content: "[Summary of previous conversation]",
            Timestamp: DateTimeOffset.UtcNow));

        session.Messages.Add(new AssistantMessage(
            Id: Guid.NewGuid().ToString("N"),
            Content: [new TextBlock(formattedSummary)],
            Timestamp: DateTimeOffset.UtcNow));

        foreach (var retained in retainedMessages)
            session.Messages.Add(MessageCloner.CloneMessage(retained));

        RunPostCompactCleanup(session);
        MafSessionInvalidator.Invalidate(session, "compact.full");
    }

    /// <summary>
    /// Partial compact: collapse the message range [<paramref name="fromIndex"/>,
    /// <paramref name="upToIndex"/>) into a boundary marker + summary, preserving
    /// the messages before and after the range.
    /// </summary>
    public void ApplyPartialCompact(
        Conversation session,
        string formattedSummary,
        int fromIndex,
        int upToIndex)
    {
        var (adjustedFromIndex, adjustedUpToIndex) = MessageApiInvariantHelper
            .AdjustRangeToAtomicBoundaries(session.Messages, fromIndex, upToIndex);
        var beforeRange = session.Messages.Take(adjustedFromIndex).ToList();
        var afterRange = session.Messages.Skip(adjustedUpToIndex).ToList();

        session.Messages.Clear();

        foreach (var msg in beforeRange)
            session.Messages.Add(MessageCloner.CloneMessage(msg));

        session.Messages.Add(new SystemMessage(
            Id: Guid.NewGuid().ToString("N"),
            Content: "[Partial conversation compaction]",
            Timestamp: DateTimeOffset.UtcNow,
            Type: SystemMessageType.Info));

        session.Messages.Add(new UserMessage(
            Id: Guid.NewGuid().ToString("N"),
            Content: "[Summary of compacted conversation range]",
            Timestamp: DateTimeOffset.UtcNow));

        session.Messages.Add(new AssistantMessage(
            Id: Guid.NewGuid().ToString("N"),
            Content: [new TextBlock(formattedSummary)],
            Timestamp: DateTimeOffset.UtcNow));

        foreach (var msg in afterRange)
            session.Messages.Add(MessageCloner.CloneMessage(msg));

        RunPostCompactCleanup(session);
        MafSessionInvalidator.Invalidate(session, "compact.partial");
    }

    private static IReadOnlyList<Message> SelectRetainedMessages(IReadOnlyList<Message> messages)
    {
        var eligible = messages
            .Where(message => message is not SystemMessage { Content: CompactConstants.CompactBoundaryContent })
            .ToList();
        if (eligible.Count <= CompactConstants.RecentMessagesToKeep)
            return eligible;

        var desiredStart = eligible.Count - CompactConstants.RecentMessagesToKeep;
        var start = MessageApiInvariantHelper
            .AdjustRangeToAtomicBoundaries(eligible, desiredStart, eligible.Count)
            .FromIndex;
        return eligible.Skip(start).ToList();
    }

    /// <summary>
    /// Keeps tool call/result messages API-valid after a history boundary is
    /// introduced. A compacted segment must never leave an orphaned result or
    /// an assistant tool call without its corresponding result.
    /// </summary>
    private static IReadOnlyList<Message> NormalizeToolPairs(
        IEnumerable<Message> messages)
    {
        var source = messages.ToList();
        var toolUseIds = source
            .OfType<AssistantMessage>()
            .SelectMany(message => message.Content.OfType<ToolUseBlock>())
            .Select(block => block.Id)
            .ToHashSet(StringComparer.Ordinal);
        var toolResultIds = source
            .OfType<ToolResultMessage>()
            .Select(message => message.ToolUseId)
            .ToHashSet(StringComparer.Ordinal);

        List<Message> normalized = [];
        foreach (var message in source)
        {
            if (message is ToolResultMessage result
                && !toolUseIds.Contains(result.ToolUseId))
                continue;

            if (message is not AssistantMessage assistant)
            {
                normalized.Add(message);
                continue;
            }

            var content = assistant.Content
                .Where(block => block is not ToolUseBlock toolUse
                    || toolResultIds.Contains(toolUse.Id))
                .ToList();

            if (content.Count > 0)
                normalized.Add(assistant with { Content = content });
        }

        return normalized;
    }

    private static void RunPostCompactCleanup(Conversation session)
    {
        while (session.Messages.Count > 3 + CompactConstants.RecentMessagesToKeep)
            session.Messages.RemoveAt(3);

        for (var index = session.Messages.Count - 1; index > 0; index--)
        {
            if (session.Messages[index] is SystemMessage current
                && session.Messages[index - 1] is SystemMessage previous
                && current.Content == previous.Content
                && current.Type == previous.Type)
            {
                session.Messages.RemoveAt(index);
            }
        }
    }
}
