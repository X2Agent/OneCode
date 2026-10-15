using OneCode.Core.Memory;
using OneCode.Core.Tools;

namespace OneCode.Infrastructure.Memory;

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
public sealed partial class MemoryEntryStore(
    IWorkingDirectoryAccessor wdAccessor,
    ILogger<MemoryEntryStore>? logger = null) : IMemoryEntryStore
{
    /// <summary>Maximum entries per scope (oldest non-manual entries evicted when exceeded).</summary>
    public const int MaxEntries = 50;

    private const string FileName = "MEMORY.md";
    private const string FrontmatterStart = "---";
    private const string EntryHeaderPrefix = "## ";

    /// <summary>Lock file that serializes writers across processes (see <see cref="EnterMutationAsync"/>).</summary>
    private const string LockFileName = ".MEMORY.lock";

    /// <summary>How many times to retry taking the cross-process lock before giving up.</summary>
    /// <remarks>
    /// The holder's critical section is one file read plus one write. A bounded retry means a stuck peer
    /// surfaces as a failure to the caller instead of hanging the agent indefinitely.
    /// </remarks>
    private const int LockRetryLimit = 50;

    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>Source value marking user-authored entries (never auto-evicted, see <see cref="PruneAsync"/>).</summary>
    private const string ManualSource = "manual";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_locks = new(StringComparer.OrdinalIgnoreCase);

    private readonly IWorkingDirectoryAccessor _wdAccessor = wdAccessor ?? throw new ArgumentNullException(nameof(wdAccessor));
    private readonly ILogger<MemoryEntryStore>? _logger = logger;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MemoryEntry>> LoadAsync(MemoryScope scope, CancellationToken ct = default)
    {
        var entries = await LoadAllAsync(scope, ct).ConfigureAwait(false);
        return entries.Where(e => !e.IsExpired).ToList();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Query path: an unreadable store degrades to an empty list so a broken file cannot break the
    /// caller (e.g. <c>/memory list</c> or prompt injection). Mutation paths must not use this
    /// degradation — they call <see cref="LoadForMutationAsync"/> so a read failure refuses the write
    /// instead of overwriting the store with an unknown baseline.
    /// </remarks>
    public async Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(MemoryScope scope, CancellationToken ct = default)
    {
        try
        {
            return await LoadForMutationAsync(scope, ct).ConfigureAwait(false);
        }
        catch (MemoryStoreReadException ex)
        {
            _logger?.LogWarning(ex, "Failed to read memory file {Path}; treating as empty", ex.FilePath);
            return [];
        }
    }

    /// <summary>
    /// Read the store for a read-modify-write operation: returns an empty list only when the file
    /// genuinely does not exist, and throws <see cref="MemoryStoreReadException"/> when it exists but
    /// cannot be read. Cancellation propagates unchanged.
    /// </summary>
    private async Task<IReadOnlyList<MemoryEntry>> LoadForMutationAsync(MemoryScope scope, CancellationToken ct)
    {
        var dir = ResolveDirectory(scope);
        var filePath = GetFilePath(dir);

        // "Not there" and "cannot read" must stay distinguishable: the first is a legitimate empty
        // baseline for a write, the second must refuse the write (see MemoryStoreReadException).
        string content;
        try
        {
            content = await File.ReadAllTextAsync(filePath, Encoding.UTF8, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MemoryStoreReadException(scope, $"Failed to read memory store '{filePath}'.", ex) { FilePath = filePath };
        }

        return ParseEntries(content);
    }

    /// <inheritdoc/>
    public async Task UpsertAsync(MemoryScope scope, IEnumerable<MemoryEntry> entries, CancellationToken ct = default)
    {
        var entryList = entries.ToList();
        if (entryList.Count == 0)
            return;

        var dir = ResolveDirectory(scope);
        using var scopeLock = await EnterMutationAsync(dir, ct).ConfigureAwait(false);

        var existing = await LoadForMutationAsync(scope, ct).ConfigureAwait(false);
        var dict = existing.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entryList)
        {
            if (dict.TryGetValue(entry.Key, out var existingEntry))
            {
                // Content update preserves the original creation time.
                dict[entry.Key] = entry with
                {
                    CreatedAt = existingEntry.CreatedAt,
                };
            }
            else
            {
                dict[entry.Key] = entry;
            }
        }

        await WriteEntriesAsync(dir, dict.Values, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveAsync(MemoryScope scope, string key, CancellationToken ct = default)
    {
        var dir = ResolveDirectory(scope);
        using var scopeLock = await EnterMutationAsync(dir, ct).ConfigureAwait(false);

        var existing = await LoadForMutationAsync(scope, ct).ConfigureAwait(false);
        var dict = existing.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);

        if (!dict.Remove(key))
            return false;

        await WriteEntriesAsync(dir, dict.Values, ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc/>
    public async Task ClearAsync(MemoryScope scope, CancellationToken ct = default)
    {
        var dir = ResolveDirectory(scope);
        using var scopeLock = await EnterMutationAsync(dir, ct).ConfigureAwait(false);

        var filePath = GetFilePath(dir);
        if (File.Exists(filePath))
            File.Delete(filePath);
    }

    /// <inheritdoc/>
    public async Task<int> PruneAsync(MemoryScope scope, CancellationToken ct = default)
    {
        var dir = ResolveDirectory(scope);
        using var scopeLock = await EnterMutationAsync(dir, ct).ConfigureAwait(false);

        var existing = await LoadForMutationAsync(scope, ct).ConfigureAwait(false);
        if (existing.Count == 0)
            return 0;

        var before = existing.Count;

        var alive = existing.Where(e => !e.IsExpired).ToList();

        // Age-ranked eviction: manual entries are exempt, then oldest UpdatedAt entries are removed first.
        if (alive.Count > MaxEntries)
        {
            var exempt = alive.Where(e => IsEvictionExempt(e)).ToList();
            var candidates = alive.Where(e => !IsEvictionExempt(e)).ToList();

            var keepCount = Math.Max(0, MaxEntries - exempt.Count);
            var survivors = candidates
                .OrderByDescending(e => e.UpdatedAt)
                .Take(keepCount)
                .ToList();

            alive = [.. exempt, .. survivors];
        }

        var removed = before - alive.Count;
        if (removed > 0)
        {
            await WriteEntriesAsync(dir, alive, ct).ConfigureAwait(false);
            _logger?.LogInformation("Pruned {Count} memory entries from {Dir}", removed, dir);
        }

        return removed;
    }

    /// <summary>
    /// Entries that eviction never removes automatically: user-authored (<c>manual</c>) entries
    /// express explicit intent. They still consume capacity, so they can crowd the store — the
    /// capacity check only counts what they leave over.
    /// </summary>
    private static bool IsEvictionExempt(MemoryEntry entry) =>
        string.Equals(entry.Source, ManualSource, StringComparison.OrdinalIgnoreCase);

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
            return [];

        var body = StripFrontmatter(content);
        List<MemoryEntry> results = [];

        var matches = EntryHeaderRegex().Matches(body);
        if (matches.Count == 0)
            return [];

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

    /// <summary>
    /// Known metadata property keys in the <c>- key: value</c> header block of an entry.
    /// Including legacy <c>hit_count</c> and <c>last_hit_at</c> so existing files parse cleanly
    /// without leaking those lines into the entry body; user-written Markdown bullet lists whose
    /// first line is not a known metadata key are preserved as entry content.
    /// </summary>
    private static readonly HashSet<string> KnownMetadataKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "source",
        "category",
        "created_at",
        "updated_at",
        "expires_at",
        "hit_count",
        "last_hit_at",
    };

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
                    if (KnownMetadataKeys.Contains(propKey))
                    {
                        var propValue = propLine[(colon + 1)..].Trim();
                        props[propKey] = propValue;
                        continue;
                    }
                }
            }

            if (valueStartIndex < 0)
                valueStartIndex = i;
        }

        var value = valueStartIndex >= 0 && valueStartIndex < lines.Length
            ? string.Join('\n', lines.Skip(valueStartIndex)).Trim()
            : string.Empty;

        var source = props.GetValueOrDefault("source") ?? ManualSource;
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
            .OrderByDescending(e => e.Source == ManualSource)  // manual first
            .ThenByDescending(e => e.UpdatedAt)
            .ToList();

        var content = SerializeEntries(entryList);
        var filePath = GetFilePath(memoryDir);

        Directory.CreateDirectory(memoryDir);

        // Unique temp name: a fixed `<file>.tmp` makes two concurrent writers race on the same path,
        // so one can replace the file while the other is still writing into the shared temp file.
        var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, content, Encoding.UTF8, ct).ConfigureAwait(false);

            if (File.Exists(filePath))
                File.Replace(tempPath, filePath, destinationBackupFileName: null);
            else
                File.Move(tempPath, filePath);
        }
        catch
        {
            // Leaving a partial temp file behind would make the next write's cleanup ambiguous.
            TryDeleteTemp(tempPath);
            throw;
        }
    }

    private void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            // Best effort: an orphaned temp file is noise, not corruption.
            _logger?.LogDebug(ex, "Failed to remove temp memory file {Path}", tempPath);
        }
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

    /// <summary>
    /// Acquires both the in-process semaphore and the cross-process file lock for a memory directory,
    /// and returns a scope that releases them in reverse order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read-modify-write must hold both: the semaphore keeps this process's async continuations from
    /// interleaving, and the file lock keeps a second process from merging against the same stale
    /// baseline.
    /// </para>
    /// <para>
    /// <b>A file lock, not a mutex.</b> <see cref="Mutex"/> ownership is thread-affine, but every
    /// <c>await</c> here can resume on a different thread pool thread, so releasing it would throw
    /// <see cref="ApplicationException"/>. A <see cref="FileStream"/> opened with
    /// <see cref="FileShare.None"/> carries no such affinity and is released on dispose from any thread.
    /// </para>
    /// </remarks>
    private async Task<MutationScope> EnterMutationAsync(string memoryDir, CancellationToken ct)
    {
        var gate = GetLock(memoryDir);
        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var lockFile = await AcquireCrossProcessLockAsync(memoryDir, ct).ConfigureAwait(false);
            return new MutationScope(gate, lockFile);
        }
        catch
        {
            // Never leave the semaphore held if the cross-process lock could not be taken.
            gate.Release();
            throw;
        }
    }

    /// <summary>
    /// Opens the memory directory's lock file with exclusive sharing, retrying briefly so a concurrent
    /// writer's short critical section does not surface as a failure.
    /// </summary>
    private static async Task<FileStream> AcquireCrossProcessLockAsync(string memoryDir, CancellationToken ct)
    {
        Directory.CreateDirectory(memoryDir);
        var lockPath = Path.Combine(memoryDir, LockFileName);

        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException) when (attempt < LockRetryLimit)
            {
                // Another process holds it. The critical section is one read plus one write, so a short
                // backoff is enough; waiting forever would hang the caller on a stuck peer.
                await Task.Delay(LockRetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Holds the in-process and cross-process write gates for one read-modify-write.</summary>
    private readonly struct MutationScope(SemaphoreSlim gate, FileStream lockFile) : IDisposable
    {
        public void Dispose()
        {
            lockFile?.Dispose();
            gate.Release();
        }
    }

    [GeneratedRegex(@"^##\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex EntryHeaderRegex();
}
