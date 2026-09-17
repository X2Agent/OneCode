using System.Text;
using Microsoft.Agents.AI;
using OneCode.Core.Memory;

namespace OneCode.App.Services.Memory;

/// <summary>
/// Builds the MAF <see cref="TextSearchProvider"/> that exposes a <c>search_memories</c> tool, letting
/// the LLM autonomously recall full entries from <c>MEMORY.md</c> via
/// <see cref="IMemoryService.FindRelevantMemoriesAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is a product <b>configuration</b> of the MAF provider, not a reimplementation: retrieval,
/// tool exposure, telemetry redaction and result formatting all come from
/// <see cref="TextSearchProvider"/>. Only the search delegate and the rendering of OneCode's
/// memory-specific metadata (scope / relevance score / source / category) are ours.
/// </para>
/// <para>
/// <see cref="TextSearchProviderOptions.SearchTime"/> is fixed to
/// <see cref="TextSearchProviderOptions.TextSearchBehavior.OnDemandFunctionCalling"/>: memory is
/// recalled only when the model asks for it, so no search runs on every turn. With
/// <see cref="TextSearchProviderOptions.RecentMessageMemoryLimit"/> left at 0 the provider keeps no
/// per-session state, so nothing extra flows through <c>AgentSession.StateBag</c>.
/// </para>
/// </remarks>
internal static class MemorySearchProviderFactory
{
    /// <summary>
    /// Tool name kept as <c>search_memories</c> so existing prompts/instructions stay valid
    /// (<see cref="TextSearchProvider"/> would otherwise default it to <c>"Search"</c>).
    /// </summary>
    private const string SearchToolName = "search_memories";

    private const string SearchToolDescription =
        "Search persistent memory entries (in MEMORY.md) for relevant context. " +
        "Use this when you need to recall user preferences, project conventions, past " +
        "decisions, lessons from failures, or any durable fact. " +
        "Pass a natural-language query describing what you are looking for.";

    private const string EmptyQueryMessage = "No memories found: empty query.";
    private const string NoResultsMessage = "No relevant memories found.";

    public static TextSearchProvider Create(
        IMemoryService memoryService,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(memoryService);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var logger = loggerFactory.CreateLogger("MemorySearchProvider");

        var options = new TextSearchProviderOptions
        {
            SearchTime = TextSearchProviderOptions.TextSearchBehavior.OnDemandFunctionCalling,
            FunctionToolName = SearchToolName,
            FunctionToolDescription = SearchToolDescription,
            ContextFormatter = FormatResults,
        };

        return new TextSearchProvider(
            (query, ct) => SearchAsync(memoryService, logger, query, ct),
            options,
            loggerFactory);
    }

    /// <summary>
    /// Search delegate. Failures degrade to an informational message rather than throwing, so a
    /// broken memory file never fails the tool call.
    /// </summary>
    /// <remarks>
    /// <see cref="TextSearchProvider"/> 把检索委托设为 internal，外部无法直接触发；本方法以
    /// <c>internal</c> 暴露（<c>InternalsVisibleTo</c>），使单元测试能覆盖与生产完全相同的
    /// 委托路径——尤其是「命中回写」这一步接线（见 <see cref="RecordHitsAsync"/>）。
    /// </remarks>
    internal static async Task<IEnumerable<TextSearchProvider.TextSearchResult>> SearchAsync(
        IMemoryService memoryService,
        ILogger logger,
        string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [Message(EmptyQueryMessage)];

        try
        {
            var matches = await memoryService
                .FindRelevantMemoriesAsync(query, cancellationToken)
                .ConfigureAwait(false);

            logger.LogDebug("search_memories returned {Count} result(s) for '{Query}'", matches.Count, query);

            await RecordHitsAsync(memoryService, matches, logger, cancellationToken).ConfigureAwait(false);
            return matches.Select(match => new TextSearchProvider.TextSearchResult
            {
                SourceName = match.Entry.Key,
                Text = match.Entry.Value.Trim(),
                // Carries scope/score/source/category through to FormatResults.
                RawRepresentation = match,
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "search_memories failed for '{Query}'", query);
            return [Message($"Memory search failed: {ex.Message}")];
        }
    }

    /// <summary>
    /// Records usage feedback for the entries this search actually returned, so retention can rank by
    /// real recall rather than by age alone.
    /// </summary>
    /// <remarks>
    /// Only explicit <c>search_memories</c> calls count. The passive prompt-index injection path
    /// (<c>MemoryService.LoadMemoryPromptAsync</c>) deliberately does not report hits: it touches every
    /// entry on every turn, which would drown the signal, whereas an on-demand search is evidence that
    /// the model found this entry relevant.
    /// </remarks>
    private static async Task RecordHitsAsync(
        IMemoryService memoryService,
        IReadOnlyList<MemoryEntryMatch> matches,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (matches.Count == 0)
            return;

        try
        {
            foreach (var group in matches.GroupBy(match => match.Scope))
            {
                var keys = group.Select(match => match.Entry.Key).ToList();
                await memoryService.RecordHitsAsync(group.Key, keys, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort bookkeeping: a failed hit write must not fail the search that produced it.
            logger.LogDebug(ex, "Recording memory hits failed; retention will fall back to age");
        }
    }

    /// <summary>Informational result that carries no match metadata (empty query / failure).</summary>
    private static TextSearchProvider.TextSearchResult Message(string text) => new() { Text = text };

    /// <summary>
    /// Renders OneCode's memory metadata. The MAF provider's built-in formatter would only emit
    /// source name and contents, which would drop scope, relevance score and category.
    /// </summary>
    private static string FormatResults(IList<TextSearchProvider.TextSearchResult> results)
    {
        if (results.Count == 0)
            return NoResultsMessage;

        // A single result without match metadata is a delegate-level message (empty query / failure).
        if (results.Count == 1 && results[0].RawRepresentation is null)
            return results[0].Text;

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Found {results.Count} relevant memor{(results.Count == 1 ? "y" : "ies")}:");

        foreach (var result in results)
        {
            if (result.RawRepresentation is not MemoryEntryMatch match)
                continue;

            var scope = match.Scope == MemoryScope.Project ? "project" : "global";
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"## {match.Entry.Key} ({scope}, score {match.RelevanceScore})");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Source: {match.Entry.Source}, Category: {match.Entry.Category}");
            sb.AppendLine(match.Entry.Value.Trim());
        }

        return sb.ToString();
    }
}
