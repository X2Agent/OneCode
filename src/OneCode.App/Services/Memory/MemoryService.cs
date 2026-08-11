using System.Text.RegularExpressions;
using OneCode.Core.Memory;

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
/// <item><see cref="MemoryFileContextProvider"/> exposes a <c>search_memories</c> tool for on-demand
/// full-content retrieval.</item>
/// </list>
/// </para>
/// </remarks>
public sealed partial class MemoryService : IMemoryService
{
    private const int MaxSummaryValueChars = 80;
    private const int MaxRelevantMemories = 6;
    private const int MaxRelevantValueChars = 500;

    [GeneratedRegex(@"[\p{L}\p{N}_-]{2,}")]
    private static partial Regex QueryTokenRegex();

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "from", "into", "about", "have", "need",
        "want", "what", "when", "where", "your", "please", "then", "than", "task", "code", "help",
        "make", "uses", "using", "used", "继续", "实现", "支持", "接入", "相关", "这个", "那个", "需要"
    };

    private readonly ILogger<MemoryService> _logger;
    private readonly IMemoryEntryStore _store;

    public MemoryService(ILogger<MemoryService> logger, IMemoryEntryStore store)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Loads memory entries from both user-level and project-level scopes and builds a
    /// prompt section with a summary index.
    /// </summary>
    public async Task<string?> LoadMemoryPromptAsync(
        string workingDirectory,
        string? query = null,
        CancellationToken ct = default)
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

        if (manualEntries.Count > 0)
        {
            sections.Add("### User memories");
            sections.Add("");
            foreach (var entry in manualEntries)
            {
                sections.Add($"- `{entry.Entry.Key}` — {Summarize(entry.Entry.Value)}");
            }
            sections.Add("");
        }

        if (autoEntries.Count > 0)
        {
            sections.Add("### Auto-recalled memories");
            sections.Add("");
            foreach (var entry in autoEntries.Take(MemoryEntryStore.MaxAutoRecalledInSummary))
            {
                sections.Add($"- `[{entry.Entry.Category}]` {Summarize(entry.Entry.Value)}");
            }
            if (autoEntries.Count > MemoryEntryStore.MaxAutoRecalledInSummary)
            {
                sections.Add($"- ... and {autoEntries.Count - MemoryEntryStore.MaxAutoRecalledInSummary} more (use search_memories tool to retrieve)");
            }
            sections.Add("");
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var relevant = FindRelevantEntries(entries, query);
            if (relevant.Count > 0)
            {
                sections.Add("### Relevant memories for this request");
                sections.Add("");
                foreach (var entry in relevant)
                {
                    var scopeLabel = entry.Scope == MemoryScope.User ? "global" : "project";
                    sections.Add($"#### {entry.Entry.Key} ({scopeLabel})");
                    sections.Add(TruncateValue(entry.Entry.Value, MaxRelevantValueChars));
                    sections.Add("");
                }
            }
        }

        sections.Add("_Use the `search_memories` tool to retrieve full memory content._");

        return string.Join('\n', sections).TrimEnd();
    }

    /// <summary>
    /// Searches memory entries by token relevance and returns full-content matches.
    /// Used by the <c>search_memories</c> tool.
    /// </summary>
    public async Task<IReadOnlyList<MemoryEntryMatch>> FindRelevantMemoriesAsync(
        string workingDirectory,
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
    public async Task<IReadOnlyList<MemoryEntryInfo>> ListMemoryEntriesAsync(
        string workingDirectory,
        CancellationToken ct = default)
    {
        var userEntries = await _store.LoadAllAsync(MemoryScope.User, ct).ConfigureAwait(false);
        var projectEntries = await _store.LoadAllAsync(MemoryScope.Project, ct).ConfigureAwait(false);

        var results = new List<MemoryEntryInfo>();
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

    // Internal helpers

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
            return Array.Empty<ScopedEntry>();

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
            return Array.Empty<string>();

        return QueryTokenRegex().Matches(query)
            .Select(match => match.Value.Trim().ToLowerInvariant())
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

    private static string Summarize(string value)
    {
        var firstLine = value.Trim().Split('\n')[0].Trim();
        if (firstLine.Length <= MaxSummaryValueChars)
            return firstLine;
        return firstLine[..MaxSummaryValueChars] + "...";
    }

    private static string TruncateValue(string value, int maxChars)
    {
        if (value.Length <= maxChars)
            return value.Trim();
        return value[..maxChars].Trim() + "...";
    }

    // Nested records

    internal sealed record ScopedEntry(MemoryEntry Entry, MemoryScope Scope)
    {
        public int RelevanceScore { get; init; }
    }
}
