using System.Text;
using System.Text.RegularExpressions;
using OneCode.Core.Memory;

namespace OneCode.App.Services.Memory;

/// <summary>
/// File-based implementation of <see cref="IMemoryEntryStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each scope maps to a directory containing <c>MEMORY.md</c>:
/// <list type="bullet">
/// <item><see cref="MemoryScope.User"/> → <c>~/.onecode/memory/MEMORY.md</c></item>
/// <item><see cref="MemoryScope.Project"/> → <c>{cwd}/.onecode/memory/MEMORY.md</c> (resolved
///   at call time via <see cref="IWorkingDirectoryAccessor"/>, so <c>/cd</c> changes take effect)</item>
/// </list>
/// </para>
///
/// <para><b>File format</b> (see <see cref="SerializeEntries"/> for the writer):</para>
/// <code>
/// ---
/// last_updated: 2024-07-16T10:00:00Z
/// entry_count: 2
/// ---
///
/// ## fact:build-command
///
/// - source: autodream
/// - category: fact
/// - created_at: 2024-07-15T10:00:00Z
/// - updated_at: 2024-07-16T10:00:00Z
/// - expires_at: 2024-10-14T10:00:00Z
///
/// Build with `dotnet build src/OneCode.sln`. Typical duration ~45s.
/// </code>
///
/// <para>
/// <b>Thread safety</b>: writes are guarded by a per-directory <see cref="SemaphoreSlim"/>.
/// Atomic file replacement (temp + rename) ensures readers never see a partial write.
/// </para>
/// </remarks>
public sealed partial class MemoryEntryStore : IMemoryEntryStore
{
    /// <summary>Maximum entries per scope (LRU eviction when exceeded).</summary>
    public const int MaxEntries = 200;

    /// <summary>Maximum auto-recalled entries included in the summary index.</summary>
    public const int MaxAutoRecalledInSummary = 8;

    private const string FileName = "MEMORY.md";
    private const string FrontmatterStart = "---";
    private const string EntryHeaderPrefix = "## ";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_locks = new(StringComparer.OrdinalIgnoreCase);

    private readonly IWorkingDirectoryAccessor _wdAccessor;
    private readonly ILogger<MemoryEntryStore>? _logger;

    public MemoryEntryStore(
        IWorkingDirectoryAccessor wdAccessor,
        ILogger<MemoryEntryStore>? logger = null)
    {
        _wdAccessor = wdAccessor ?? throw new ArgumentNullException(nameof(wdAccessor));
        _logger = logger;
    }

    // IMemoryEntryStore

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MemoryEntry>> LoadAsync(MemoryScope scope, CancellationToken ct = default)
    {
        var entries = await LoadAllAsync(scope, ct).ConfigureAwait(false);
        return entries.Where(e => !e.IsExpired).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(MemoryScope scope, CancellationToken ct = default)
    {
        var dir = ResolveDirectory(scope);
        var filePath = GetFilePath(dir);
        if (!File.Exists(filePath))
            return Array.Empty<MemoryEntry>();

        try
        {
            var content = await File.ReadAllTextAsync(filePath, Encoding.UTF8, ct).ConfigureAwait(false);
            return ParseEntries(content);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read memory file {Path}", filePath);
            return Array.Empty<MemoryEntry>();
        }
    }

    /// <inheritdoc/>
    public async Task UpsertAsync(MemoryScope scope, IEnumerable<MemoryEntry> entries, CancellationToken ct = default)
    {
        var entryList = entries.ToList();
        if (entryList.Count == 0)
            return;

        var dir = ResolveDirectory(scope);
        var gate = GetLock(dir);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await LoadAllAsync(scope, ct).ConfigureAwait(false);
            var dict = existing.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entryList)
            {
                if (dict.TryGetValue(entry.Key, out var existingEntry))
                {
                    // Preserve original CreatedAt on update
                    dict[entry.Key] = entry with { CreatedAt = existingEntry.CreatedAt };
                }
                else
                {
                    dict[entry.Key] = entry;
                }
            }

            await WriteEntriesAsync(dir, dict.Values, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAsync(MemoryScope scope, string key, CancellationToken ct = default)
    {
        var dir = ResolveDirectory(scope);
        var gate = GetLock(dir);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await LoadAllAsync(scope, ct).ConfigureAwait(false);
            var dict = existing.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);

            if (!dict.Remove(key))
                return false;

            await WriteEntriesAsync(dir, dict.Values, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task ClearAsync(MemoryScope scope, CancellationToken ct = default)
    {
        var dir = ResolveDirectory(scope);
        var gate = GetLock(dir);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var filePath = GetFilePath(dir);
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<int> PruneAsync(MemoryScope scope, CancellationToken ct = default)
    {
        var dir = ResolveDirectory(scope);
        var gate = GetLock(dir);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await LoadAllAsync(scope, ct).ConfigureAwait(false);
            if (existing.Count == 0)
                return 0;

            var before = existing.Count;

            var alive = existing.Where(e => !e.IsExpired).ToList();

            // LRU eviction: if still over limit, drop oldest by UpdatedAt
            if (alive.Count > MaxEntries)
            {
                alive = alive
                    .OrderByDescending(e => e.UpdatedAt)
                    .Take(MaxEntries)
                    .ToList();
            }

            var removed = before - alive.Count;
            if (removed > 0)
            {
                await WriteEntriesAsync(dir, alive, ct).ConfigureAwait(false);
                _logger?.LogInformation("Pruned {Count} memory entries from {Dir}", removed, dir);
            }

            return removed;
        }
        finally
        {
            gate.Release();
        }
    }

    // Scope → directory resolution

    /// <summary>
    /// Resolves the physical directory for a scope.
    /// <see cref="MemoryScope.User"/> is fixed (global); <see cref="MemoryScope.Project"/>
    /// reads the current <see cref="IWorkingDirectoryAccessor.WorkingDirectory"/> at call time.
    /// </summary>
    internal string ResolveDirectory(MemoryScope scope)
    {
        return scope switch
        {
            MemoryScope.User => MemdirPaths.UserMemoryDir,
            MemoryScope.Project => MemdirPaths.ProjectMemoryDir(_wdAccessor.WorkingDirectory),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
        };
    }

    internal static string GetFilePath(string memoryDir) => Path.Combine(memoryDir, FileName);

    // Parsing

    /// <summary>
    /// Parses MEMORY.md content into a list of <see cref="MemoryEntry"/> records.
    /// Tolerant of missing frontmatter and partial entries.
    /// </summary>
    internal static IReadOnlyList<MemoryEntry> ParseEntries(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return Array.Empty<MemoryEntry>();

        var body = StripFrontmatter(content);
        var results = new List<MemoryEntry>();

        var matches = EntryHeaderRegex().Matches(body);
        if (matches.Count == 0)
            return Array.Empty<MemoryEntry>();

        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var key = match.Groups[1].Value.Trim();
            var start = match.Index + match.Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : body.Length;
            var entryBody = body[start..end].Trim();

            var entry = ParseEntry(key, entryBody);
            if (entry is not null)
                results.Add(entry);
        }

        return results;
    }

    private static string StripFrontmatter(string content)
    {
        if (!content.StartsWith(FrontmatterStart, StringComparison.Ordinal))
            return content;

        var endIndex = content.IndexOf("\n---", FrontmatterStart.Length, StringComparison.Ordinal);
        if (endIndex < 0)
            return content;

        var afterFrontmatter = content[(endIndex + 4)..];
        return afterFrontmatter.TrimStart('\r', '\n');
    }

    private static MemoryEntry? ParseEntry(string key, string body)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var lines = body.Split('\n');
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var valueStartIndex = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (string.IsNullOrEmpty(trimmed))
            {
                if (valueStartIndex < 0 && props.Count > 0)
                {
                    valueStartIndex = i + 1;
                    continue;
                }
                if (valueStartIndex >= 0)
                    continue;
                continue;
            }

            if (valueStartIndex < 0 && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                var propLine = trimmed[2..];
                var colon = propLine.IndexOf(':');
                if (colon > 0)
                {
                    var propKey = propLine[..colon].Trim();
                    var propValue = propLine[(colon + 1)..].Trim();
                    props[propKey] = propValue;
                }
                continue;
            }

            if (valueStartIndex < 0)
                valueStartIndex = i;
        }

        string value;
        if (valueStartIndex >= 0 && valueStartIndex < lines.Length)
        {
            var valueLines = lines.Skip(valueStartIndex);
            value = string.Join('\n', valueLines).Trim();
        }
        else
        {
            value = string.Empty;
        }

        var source = props.GetValueOrDefault("source") ?? "manual";
        var category = props.GetValueOrDefault("category") ?? MemoryEntry.DeriveCategory(key);
        var createdAt = ParseDateTime(props.GetValueOrDefault("created_at")) ?? DateTimeOffset.UtcNow;
        var updatedAt = ParseDateTime(props.GetValueOrDefault("updated_at")) ?? createdAt;
        var expiresAt = ParseDateTime(props.GetValueOrDefault("expires_at"));

        if (string.IsNullOrWhiteSpace(value))
            return null;

        return new MemoryEntry
        {
            Key = key,
            Value = value,
            Source = source,
            Category = category,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            ExpiresAt = expiresAt,
        };
    }

    private static DateTimeOffset? ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "never", StringComparison.OrdinalIgnoreCase))
            return null;

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
    }

    // Serialization

    private async Task WriteEntriesAsync(
        string memoryDir,
        IEnumerable<MemoryEntry> entries,
        CancellationToken ct)
    {
        var entryList = entries
            .OrderByDescending(e => e.Source == "manual")  // manual first
            .ThenByDescending(e => e.UpdatedAt)
            .ToList();

        var content = SerializeEntries(entryList);
        var filePath = GetFilePath(memoryDir);

        Directory.CreateDirectory(memoryDir);

        var tempPath = filePath + ".tmp";
        await File.WriteAllTextAsync(tempPath, content, Encoding.UTF8, ct).ConfigureAwait(false);

        if (File.Exists(filePath))
            File.Replace(tempPath, filePath, destinationBackupFileName: null);
        else
            File.Move(tempPath, filePath);
    }

    internal static string SerializeEntries(IReadOnlyList<MemoryEntry> entries)
    {
        var sb = new StringBuilder();

        sb.AppendLine("---");
        sb.AppendLine(CultureInfo.InvariantCulture, $"last_updated: {DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"entry_count: {entries.Count}");
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var entry in entries)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{EntryHeaderPrefix}{entry.Key}");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"- source: {entry.Source}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- category: {entry.Category}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- created_at: {entry.CreatedAt.ToString("O", CultureInfo.InvariantCulture)}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- updated_at: {entry.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)}");

            if (entry.ExpiresAt.HasValue)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- expires_at: {entry.ExpiresAt.Value.ToString("O", CultureInfo.InvariantCulture)}");

            sb.AppendLine();
            sb.AppendLine(entry.Value.Trim());
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static SemaphoreSlim GetLock(string memoryDir)
    {
        var normalized = Path.GetFullPath(memoryDir);
        return s_locks.GetOrAdd(normalized, _ => new SemaphoreSlim(1, 1));
    }

    [GeneratedRegex(@"^##\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex EntryHeaderRegex();
}
