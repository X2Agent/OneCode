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

    /// <summary>
    /// Total character budget for one formatted result set.
    /// </summary>
    /// <remarks>
    /// Matches the budget the index side uses: a search must not be able to spend more context than the
    /// index it replaces. Entries that do not fit are reported as omitted rather than truncated.
    /// </remarks>
    private const int MaxResultChars = 12_000;

    /// <summary>
    /// Shown when the store could not be read. Deliberately says "unavailable" rather than "none":
    /// the model must not treat an unreadable store as an empty one and start re-learning facts.
    /// </summary>
    private const string MemoryUnavailableMessage =
        "Memory search is temporarily unavailable: the memory store could not be read. " +
        "Do not conclude that no memories exist.";

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
    /// <para>
    /// <see cref="TextSearchProvider"/> 把检索委托设为 internal，外部无法直接触发；本方法以
    /// <c>internal</c> 暴露（<c>InternalsVisibleTo</c>），使单元测试能覆盖与生产完全相同的
    /// 委托路径——尤其是「命中回写」这一步接线（见 <see cref="RecordHitsAsync"/>）。
    /// </para>
    /// <para>
    /// <b>取消不是失败。</b> <see cref="OperationCanceledException"/> 必须继续传播，否则调用方取消会
    /// 变成一条看起来正常的工具结果，模型会把它当成真实检索输出。
    /// </para>
    /// <para>
    /// <b>隐私边界。</b> 日志不记原始 query（用户提问可能含敏感内容），工具结果不回传异常原文
    /// （可能含文件路径与内容片段）。稳定错误分类足够让模型判断是否需要重试。MAF 对自身 logger 的
    /// 脱敏不覆盖产品委托产生的日志与结果。
    /// </para>
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

            logger.LogDebug("search_memories returned {Count} result(s)", matches.Count);

            await RecordHitsAsync(memoryService, matches, logger, cancellationToken).ConfigureAwait(false);
            return matches.Select(match => new TextSearchProvider.TextSearchResult
            {
                SourceName = match.Entry.Key,
                Text = match.Entry.Value.Trim(),
                // Carries scope/score/source/category through to FormatResults.
                RawRepresentation = match,
            });
        }
        catch (OperationCanceledException)
        {
            // Cancellation stays cancellation: the caller asked to stop, it did not fail.
            throw;
        }
        catch (MemoryStoreReadException ex)
        {
            // The store exists but is unreadable. Reporting "no memories" would let the model
            // conclude the knowledge is absent instead of unavailable.
            logger.LogWarning(ex, "search_memories could not read the memory store ({Scope})", ex.Scope);
            return [Message(MemoryUnavailableMessage)];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "search_memories failed");
            return [Message(MemoryUnavailableMessage)];
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
    /// <remarks>
    /// Output is bounded: a search returning several large entries would otherwise consume the very
    /// context the tool exists to inform. Entries that do not fit are counted and reported as omitted,
    /// so the model knows the result set was truncated instead of assuming it saw everything.
    /// </remarks>
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

        var remaining = MaxResultChars;
        var omitted = 0;

        foreach (var result in results)
        {
            if (result.RawRepresentation is not MemoryEntryMatch match)
                continue;

            var scope = match.Scope == MemoryScope.Project ? "project" : "global";
            var header = $"## {match.Entry.Key} ({scope}, score {match.RelevanceScore})";
            var meta = $"Source: {match.Entry.Source}, Category: {match.Entry.Category}";
            var body = match.Entry.Value.Trim();

            var cost = header.Length + meta.Length + body.Length;
            if (cost > remaining)
            {
                omitted++;
                continue;
            }

            remaining -= cost;
            sb.AppendLine();
            sb.AppendLine(header);
            sb.AppendLine(meta);
            sb.AppendLine(body);
        }

        if (omitted > 0)
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"... {omitted} more match(es) omitted (result budget reached; narrow the query to see them)");
        }

        return sb.ToString();
    }
}
