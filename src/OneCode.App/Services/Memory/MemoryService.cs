using OneCode.Core.Memory;

namespace OneCode.App.Services.Memory;

/// <summary>
/// Loads structured memory entries via <see cref="IMemoryEntryStore"/> and builds full-content prompt sections
/// for <see cref="MemoryContextProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// All memories (manual + AutoDream-extracted) are stored as entries in the memory backend.
/// <see cref="IMemoryEntryStore"/> abstracts the physical storage (MEMORY.md files) —
/// this service only deals with entries and scopes.
/// </para>
/// <para>
/// <b>Context injection strategy</b>:
/// <see cref="MemoryContextProvider"/> calls <see cref="LoadMemoryPromptAsync"/> on every agent invocation
/// and injects the full active memory content via <c>AIContext.Instructions</c> (stripped of internal
/// storage metadata such as <c>source</c>, <c>updated_at</c>, and <c>expires_at</c>), bounded by
/// <see cref="MaxMemoryContextChars"/>.
/// </para>
/// </remarks>
public sealed class MemoryService(ILogger<MemoryService> logger, IMemoryEntryStore store)
    : IMemoryService
{
    /// <summary>
    /// Total character budget for the injected memory context (~2,000 tokens).
    /// </summary>
    /// <remarks>
    /// Bounds context consumption across every turn while allowing full bodies of up to ~50 active
    /// entries to be injected directly without a secondary search tool round-trip.
    /// </remarks>
    public const int MaxMemoryContextChars = 8_000;

    private readonly ILogger<MemoryService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IMemoryEntryStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// Loads non-expired memory entries from both user-level and project-level scopes and builds a
    /// clean full-content prompt section bounded by <see cref="MaxMemoryContextChars"/>.
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

        var manualEntries = OrderByScopeAndRecency(entries.Where(e => e.Entry.Source == "manual"));
        var autoEntries = OrderByScopeAndRecency(entries.Where(e => e.Entry.Source != "manual"));

        var budget = MaxMemoryContextChars;

        if (manualEntries.Count > 0)
        {
            sections.Add("### User memories");
            sections.Add("");
            foreach (var entry in manualEntries)
            {
                var scopeLabel = entry.Scope == MemoryScope.Project ? "project" : "global";
                var block = $"- `{entry.Entry.Key}` ({scopeLabel}) — {entry.Entry.Value.Trim()}";
                if (!TrySpend(ref budget, block.Length))
                    break;

                sections.Add(block);
            }
            sections.Add("");
        }

        if (autoEntries.Count > 0)
        {
            sections.Add("### Auto-recalled memories");
            sections.Add("");

            var shown = 0;
            foreach (var entry in autoEntries)
            {
                var scopeLabel = entry.Scope == MemoryScope.Project ? "project" : "global";
                var block = $"- `{entry.Entry.Key}` `[{entry.Entry.Category}]` ({scopeLabel}) — {entry.Entry.Value.Trim()}";
                if (!TrySpend(ref budget, block.Length))
                    break;

                sections.Add(block);
                shown++;
            }

            if (shown < autoEntries.Count)
            {
                sections.Add($"- ... and {autoEntries.Count - shown} more omitted (memory context budget reached)");
            }
            sections.Add("");
        }

        return string.Join('\n', sections).TrimEnd();
    }

    /// <summary>
    /// Lists all memory entries (including expired) for management commands.
    /// </summary>
    public async Task<IReadOnlyList<MemoryEntryInfo>> ListMemoryEntriesAsync(CancellationToken ct = default)
    {
        var projectEntries = await _store.LoadAllAsync(MemoryScope.Project, ct).ConfigureAwait(false);
        var userEntries = await _store.LoadAllAsync(MemoryScope.User, ct).ConfigureAwait(false);

        List<MemoryEntryInfo> results = [];
        var index = 1;

        foreach (var entry in projectEntries.OrderByDescending(e => e.UpdatedAt))
        {
            results.Add(new MemoryEntryInfo(index++, entry, "project"));
        }

        foreach (var entry in userEntries.OrderByDescending(e => e.UpdatedAt))
        {
            results.Add(new MemoryEntryInfo(index++, entry, "global"));
        }

        return results;
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

    /// <summary>
    /// Project entries first: project knowledge applies to the work at hand, user-level entries
    /// are background. Ties broken by most recently updated first.
    /// </summary>
    private static List<ScopedEntry> OrderByScopeAndRecency(IEnumerable<ScopedEntry> source) =>
        source
            .OrderByDescending(e => e.Scope == MemoryScope.Project)
            .ThenByDescending(e => e.Entry.UpdatedAt)
            .ToList();

    /// <summary>
    /// Spends from a character budget, refusing the entry when it would overrun it.
    /// </summary>
    private static bool TrySpend(ref int remaining, int cost)
    {
        if (cost > remaining)
            return false;

        remaining -= cost;
        return true;
    }

    internal sealed record ScopedEntry(MemoryEntry Entry, MemoryScope Scope);
}
