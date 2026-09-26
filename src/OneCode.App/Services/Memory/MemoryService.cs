using OneCode.Core.Memory;
using OneCode.Core.Text;
using OneCode.Infrastructure.Memory;

namespace OneCode.App.Services.Memory;

/// <summary>
/// Loads structured memory entries via <see cref="IMemoryEntryStore"/> and builds prompt sections.
/// </summary>
/// <remarks>
/// <para>
/// All memories (manual + AutoDream-extracted) are stored as entries in the memory backend.
/// <see cref="IMemoryEntryStore"/> abstracts the physical storage (MEMORY.md files) —
/// this service only deals with entries and scopes.
/// </para>
/// <para>
/// <b>System prompt injection strategy</b>:
/// <list type="bullet">
/// <item>The full index summary (key + value first line) is injected so the LLM knows what's available.</item>
/// <item><see cref="MemorySearchProviderFactory"/> exposes a <c>search_memories</c> tool for on-demand
/// full-content retrieval.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class MemoryService(ILogger<MemoryService> logger, IMemoryEntryStore store)
 : IMemoryService
{
    private const int MaxSummaryValueChars = 80;
    private const int MaxRelevantMemories = 6;

    /// <summary>
    /// Total character budget for the injected memory index.
    /// </summary>
    /// <remarks>
    /// Entry count is not a token bound: 200 short lines and 200 long ones cost very different amounts
    /// of context, and the index is injected on <b>every</b> turn. A character budget is what actually
    /// caps the cost; the per-entry limit only shapes each line.
    /// </remarks>
    private const int MaxIndexChars = 4_000;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "from", "into", "about", "have", "need",
        "want", "what", "when", "where", "your", "please", "then", "than", "task", "code", "help",
        "make", "uses", "using", "used", "继续", "实现", "支持", "接入", "相关", "这个", "那个", "需要"
    };

    private readonly ILogger<MemoryService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IMemoryEntryStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// Loads memory entries from both user-level and project-level scopes and builds a
    /// prompt section with a summary index.
    /// </summary>
    public async Task<string?> LoadMemoryPromptAsync(CancellationToken ct = default)
    {
        var entries = await LoadAllScopedEntriesAsync(ct).ConfigureAwait(false);

        if (entries.Count == 0)
            return null;

        var sections = new List<string>
        {
            "## Memory",
            "",
            "Memories are stored per-scope: user-level (global) and project-level (current working directory).",
            "",
        };

        var manualEntries = entries.Where(e => e.Entry.Source == "manual").ToList();
        var autoEntries = entries.Where(e => e.Entry.Source != "manual").ToList();

        var budget = MaxIndexChars;

        if (manualEntries.Count > 0)
        {
            sections.Add("### User memories");
            sections.Add("");
            foreach (var entry in manualEntries)
            {
                var line = $"- `{entry.Entry.Key}` — {Summarize(entry.Entry.Value)}";
                if (!TrySpend(ref budget, line.Length))
                    break;

                sections.Add(line);
            }
            sections.Add("");
        }

        if (autoEntries.Count > 0)
        {
            sections.Add("### Auto-recalled memories");
            sections.Add("");

            // Project entries first: project knowledge applies to the work at hand, user-level entries
            // are background. Without this the newest-N slice let a busy user-level store crowd out the
            // project's own conventions.
            var ordered = autoEntries
                .OrderByDescending(e => e.Scope == MemoryScope.Project)
                .ThenByDescending(e => e.Entry.UpdatedAt)
                .ToList();

            var shown = 0;
            foreach (var entry in ordered.Take(MemoryEntryStore.MaxAutoRecalledInSummary))
            {
                var scopeLabel = entry.Scope == MemoryScope.Project ? "project" : "global";
                var line = $"- `[{entry.Entry.Category}]` ({scopeLabel}) {Summarize(entry.Entry.Value)}";
                if (!TrySpend(ref budget, line.Length))
                    break;

                sections.Add(line);
                shown++;
            }

            if (shown < autoEntries.Count)
            {
                sections.Add($"- ... and {autoEntries.Count - shown} more (use search_memories tool to retrieve)");
            }
            sections.Add("");
        }

        sections.Add("_Use the `search_memories` tool to retrieve full memory content._");

        return string.Join('\n', sections).TrimEnd();
    }

    /// <summary>
    /// Searches memory entries by token relevance and returns full-content matches.
    /// Used by the <c>search_memories</c> tool.
    /// </summary>
    public async Task<IReadOnlyList<MemoryEntryMatch>> FindRelevantMemoriesAsync(
        string query,
        CancellationToken ct = default)
    {
        var entries = await LoadAllScopedEntriesAsync(ct).ConfigureAwait(false);
        return FindRelevantEntries(entries, query)
            .Select(e => new MemoryEntryMatch(e.Entry, e.Scope, e.RelevanceScore))
            .ToList();
    }

    /// <summary>
    /// Lists all memory entries (including expired) for management commands.
    /// </summary>
    public async Task<IReadOnlyList<MemoryEntryInfo>> ListMemoryEntriesAsync(CancellationToken ct = default)
    {
        var userEntries = await _store.LoadAllAsync(MemoryScope.User, ct).ConfigureAwait(false);
        var projectEntries = await _store.LoadAllAsync(MemoryScope.Project, ct).ConfigureAwait(false);

        List<MemoryEntryInfo> results = [];
        var index = 1;

        foreach (var entry in userEntries)
        {
            results.Add(new MemoryEntryInfo(index++, entry, "global"));
        }

        foreach (var entry in projectEntries)
        {
            results.Add(new MemoryEntryInfo(index++, entry, "project"));
        }

        return results;
    }

    /// <inheritdoc/>
    public Task RecordHitsAsync(MemoryScope scope, IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        if (keys.Count == 0)
            return Task.CompletedTask;

        return _store.RecordHitsAsync(scope, keys, ct);
    }

    private async Task<IReadOnlyList<ScopedEntry>> LoadAllScopedEntriesAsync(CancellationToken ct)
    {
        var userEntries = await _store.LoadAsync(MemoryScope.User, ct).ConfigureAwait(false);
        var projectEntries = await _store.LoadAsync(MemoryScope.Project, ct).ConfigureAwait(false);

        var results = new List<ScopedEntry>(userEntries.Count + projectEntries.Count);
        results.AddRange(userEntries.Select(e => new ScopedEntry(e, MemoryScope.User)));
        results.AddRange(projectEntries.Select(e => new ScopedEntry(e, MemoryScope.Project)));
        return results;
    }

    private static IReadOnlyList<ScopedEntry> FindRelevantEntries(
        IReadOnlyList<ScopedEntry> entries,
        string? query)
    {
        var tokens = Tokenize(query);
        if (tokens.Count == 0)
            return [];

        return entries
            .Select(e => e with { RelevanceScore = Score(e, tokens) })
            .Where(e => e.RelevanceScore > 0)
            .OrderByDescending(e => e.RelevanceScore)
            .ThenByDescending(e => e.Entry.UpdatedAt)
            .Take(MaxRelevantMemories)
            .ToList();
    }

    private static IReadOnlyList<string> Tokenize(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        // 分词规则与工具检索共用 TextTokenizer（拉丁大小写边界 + 去复数 + CJK 二字滑窗）。
        // 旧实现用 \p{L}{2,} 取连续串，中文整句会变成一个 token：既过滤掉全部中文
        // （长度 ≥ 2 也匹配不上），又让任何中文子串查询召回为空。
        return TextTokenizer.Tokenize(query)
            .Where(token => token.Length >= 2 && !StopWords.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static int Score(ScopedEntry scopedEntry, IReadOnlyList<string> tokens)
    {
        var entry = scopedEntry.Entry;
        var key = entry.Key.ToLowerInvariant();
        var value = entry.Value.ToLowerInvariant();
        var score = 0;

        foreach (var token in tokens)
        {
            if (key.Contains(token, StringComparison.Ordinal))
                score += 6;

            var occurrences = CountOccurrences(value, token);
            if (occurrences > 0)
                score += Math.Min(occurrences, 5) * 3;
        }

        if (score == 0)
            return 0;

        score += scopedEntry.Scope == MemoryScope.Project ? 2 : 1;
        score += entry.Source == "manual" ? 2 : 0;

        return score;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle))
            return 0;

        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>
    /// Spends from a character budget, refusing the line when it would overrun it.
    /// </summary>
    /// <remarks>
    /// A line that does not fit is skipped rather than truncated: a half-written index entry is worse
    /// than an absent one, because the model cannot tell it was cut.
    /// </remarks>
    private static bool TrySpend(ref int remaining, int cost)
    {
        if (cost > remaining)
            return false;

        remaining -= cost;
        return true;
    }

    private static string Summarize(string value)
    {
        var firstLine = value.Trim().Split('\n')[0].Trim();
        if (firstLine.Length <= MaxSummaryValueChars)
            return firstLine;
        return firstLine[..MaxSummaryValueChars] + "...";
    }

    // Nested records

    internal sealed record ScopedEntry(MemoryEntry Entry, MemoryScope Scope)
    {
        public int RelevanceScore { get; init; }
    }
}
