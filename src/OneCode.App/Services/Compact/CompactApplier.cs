namespace OneCode.App.Services.Compact;

/// <summary>
/// Applies the result of a compact run to the <see cref="Conversation"/>:
/// replaces the message history with the boundary marker + summary, restores the
/// protected tail, and trims duplicate system markers left over from previous compactions.
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

        RemoveDuplicateSystemMarkers(session);
        MafSessionInvalidator.Invalidate(session, "compact.full");
    }

    /// <summary>
    /// Partial compact: collapse the message range [<paramref name="fromIndex"/>, <paramref name="upToIndex"/>) 
    /// into a boundary marker + summary, preserving the messages before and after the range.
    /// </summary>
    /// <param name="session">Conversation whose message history is rewritten.</param>
    /// <param name="formattedSummary">Summary text that replaces the collapsed range.</param>
    /// <param name="fromIndex">Range start. Expanded outward to the enclosing atomic tool group.</param>
    /// <param name="upToIndex">Range end (exclusive). Expanded outward to the enclosing atomic tool group.</param>
    /// <remarks>
    /// The range passed in must be the <b>same</b> range whose messages were sent to the summariser.
    /// Callers resolve it once via <see cref="MessageApiInvariantHelper.AdjustRangeToAtomicBoundaries"/>;
    /// the normalisation re-applied here is <b>idempotent</b> — an already-aligned boundary sits exactly on
    /// a group edge, which the helper leaves untouched — so it cannot expand the range past what the model
    /// summarised. That idempotency is what makes this defensive call safe, and it is asserted by
    /// <c>CompactApplierRangeTests.AdjustRangeToAtomicBoundaries_IsIdempotent</c>; if it ever regresses,
    /// this call would start replacing messages the model never saw.
    /// </remarks>
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

        // Deliberately no count-based trimming here: a partial compact owns only its range, and
        // trimming by message count would silently delete messages outside it.
        RemoveDuplicateSystemMarkers(session);
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

    /// <summary>
    /// Removes consecutive duplicate system markers left over from previous compactions.
    /// </summary>
    /// <remarks>
    /// Full compact already selects the retained tail by atomic boundaries, so it needs no
    /// count-based trimming: an earlier <c>while (Count &gt; 3 + RecentMessagesToKeep) RemoveAt(3)</c>
    /// would cut the retained tail by message count and could split an atomic tool group that
    /// <see cref="NormalizeToolPairs"/> had just made valid.
    /// </remarks>
    private static void RemoveDuplicateSystemMarkers(Conversation session)
    {
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
