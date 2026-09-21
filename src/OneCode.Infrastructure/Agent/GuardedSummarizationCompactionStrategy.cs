using Microsoft.Agents.AI.Compaction;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// Wraps MAF's <see cref="SummarizationCompactionStrategy"/> so a failed, empty or
/// non-beneficial summary never replaces the original messages.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an adapter is needed.</b> The native strategy restores its excluded groups when the LLM call
/// throws, but two paths bypass that recovery:
/// </para>
/// <list type="bullet">
///   <item><description>
///     An empty response is replaced by the literal <c>[Summary unavailable]</c> and committed, so the
///     original messages are destroyed and replaced by a placeholder that carries no information.
///   </description></item>
///   <item><description>
///     A summary larger than the messages it replaces is inserted anyway. Summarisation that grows the
///     context defeats its own purpose and consumes the budget it was meant to free.
///   </description></item>
/// </list>
/// <para>
/// <b>Scope.</b> This guards only the commit decision; grouping, indexing, prompt construction and the
/// LLM call remain MAF's. Cancellation propagates unchanged, and the index is left exactly as it was
/// found whenever the summary is rejected.
/// </para>
/// </remarks>
public sealed class GuardedSummarizationCompactionStrategy : CompactionStrategy
{
    private readonly SummarizationCompactionStrategy _inner;

    /// <param name="inner">The native strategy whose result is validated before being kept.</param>
    /// <param name="trigger">
    /// Trigger evaluated by the base class. Must match the trigger given to <paramref name="inner"/>,
    /// otherwise this guard would decide whether to run on different terms than the work it guards.
    /// </param>
    /// <param name="target">Target condition, matching the one given to <paramref name="inner"/>.</param>
    public GuardedSummarizationCompactionStrategy(
        SummarizationCompactionStrategy inner,
        CompactionTrigger trigger,
        CompactionTrigger? target = null)
        : base(trigger, target)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc/>
    protected override async ValueTask<bool> CompactCoreAsync(
        CompactionMessageIndex index,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Snapshot before delegating: the inner strategy mutates groups in place and inserts its
        // summary group in the middle of the list, so restoring needs the original sequence rather
        // than just trimming appended items.
        var groups = index.Groups.ToList();
        var exclusion = groups.Select(group => group.IsExcluded).ToList();
        var reasons = groups.Select(group => group.ExcludeReason).ToList();
        var previouslyIncluded = new HashSet<CompactionMessageGroup>(
            groups.Where((_, i) => !exclusion[i]),
            ReferenceEqualityComparer.Instance);

        var changed = await _inner.CompactAsync(index, logger, cancellationToken).ConfigureAwait(false);

        if (changed && IsAcceptable(index, previouslyIncluded))
            return true;

        Restore(index, groups, exclusion, reasons);
        return false;
    }

    /// <summary>
    /// MAF writes this literal when the summariser returned nothing usable. It is shorter than the
    /// content it replaces, so a size check alone would accept it and destroy the original messages.
    /// </summary>
    private const string UnavailablePlaceholder = "[Summary unavailable]";

    /// <summary>
    /// MAF prefixes the summary message with this marker before the summary text.
    /// </summary>
    private const string SummaryPrefix = "[Summary]";

    /// <summary>
    /// A summary is acceptable only when it carries real content and is smaller than the content it
    /// replaced. Empty, placeholder or oversized summaries leave the original messages in place.
    /// </summary>
    private static bool IsAcceptable(
        CompactionMessageIndex index,
        HashSet<CompactionMessageGroup> previouslyIncluded)
    {
        var summaryGroups = index.Groups
            .Where(group => group.Kind == CompactionGroupKind.Summary)
            .ToList();

        if (summaryGroups.Count == 0 || !HasUsableContent(summaryGroups))
            return false;

        // Replaced bytes = content that was in the context before the call and is excluded now.
        // Matched by group identity, not by list position: the native strategy inserts its summary
        // group in the middle of the list (at the first group it summarized), so every later group
        // shifts by one and a positional comparison would pair each group with its successor —
        // counting only the last replaced group and rejecting summaries that really did shrink.
        var replacedBytes = index.Groups
            .Where(group => group.IsExcluded && previouslyIncluded.Contains(group))
            .Sum(group => group.ByteCount);

        var summaryBytes = summaryGroups.Sum(group => group.ByteCount);

        return replacedBytes > 0 && summaryBytes < replacedBytes;
    }

    /// <summary>
    /// Rejects a summary that has no informational value: an empty response, or the placeholder MAF
    /// substitutes for one. Committing either replaces real history with nothing.
    /// </summary>
    private static bool HasUsableContent(List<CompactionMessageGroup> summaryGroups)
    {
        foreach (var group in summaryGroups)
        {
            foreach (var message in group.Messages)
            {
                var text = message.Text;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var body = text.StartsWith(SummaryPrefix, StringComparison.Ordinal)
                    ? text[SummaryPrefix.Length..].Trim()
                    : text.Trim();

                if (body.Length == 0
                    || body.Equals(UnavailablePlaceholder, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rebuilds the index from the pre-call snapshot. The summary group is inserted mid-list, so the
    /// original group sequence is restored wholesale rather than trimmed.
    /// </summary>
    private static void Restore(
        CompactionMessageIndex index,
        List<CompactionMessageGroup> groups,
        List<bool> exclusion,
        List<string?> reasons)
    {
        index.Groups.Clear();
        for (var i = 0; i < groups.Count; i++)
        {
            groups[i].IsExcluded = exclusion[i];
            groups[i].ExcludeReason = reasons[i];
            index.Groups.Add(groups[i]);
        }
    }
}
