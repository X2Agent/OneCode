using OneCode.Core.Build;
using OneCode.Core.Domain;

namespace OneCode.Infrastructure.Build;

/// <summary>
/// Reliable JSON checkpoint and append-only state-event store for BuildRun aggregates.
/// Checkpoint and event files retain the last valid primary snapshot as <c>.bak</c>.
/// The event sequence is canonical when a process stops between event and checkpoint writes.
/// </summary>
public sealed class JsonBuildRunStore : IBuildRunStore, IBuildRunEventStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _baseDirectory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.Ordinal);

    public JsonBuildRunStore(string? basePath = null)
    {
        _baseDirectory = basePath ?? Path.Combine(PathsHelper.GetUserConfigDir(), "build-runs");
        Directory.CreateDirectory(_baseDirectory);
    }

    public Task<BuildRun?> LoadAsync(SessionId? conversationId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (conversationId is not { } id)
            return Task.FromResult<BuildRun?>(null);

        return Task.FromResult(LoadCurrent(GetRunFilePath(id), expectedConversationId: id));
    }

    public Task SaveAsync(BuildRun run, long expectedVersion, CancellationToken ct = default)
        => SaveCoreAsync(run, expectedVersion, requiredFencingToken: null, isClaim: false, ct);

    public async Task<BuildRun> ClaimWorkflowAsync(
        BuildRunId runId,
        long fencingToken,
        long expectedVersion,
        CancellationToken ct = default)
    {
        if (fencingToken <= 0)
            throw new ArgumentOutOfRangeException(nameof(fencingToken));
        var current = await LoadByIdAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"BuildRun '{runId}' was not found.");
        if (current.Version != expectedVersion)
        {
            throw new InvalidOperationException(
                $"Concurrency conflict: expected version {expectedVersion} but found {current.Version}.");
        }
        if (current.WorkflowFencingToken is { } existingToken && fencingToken <= existingToken)
            throw new InvalidOperationException("Stale BuildRun workflow fencing token.");

        var claimed = current with { WorkflowFencingToken = fencingToken };
        await SaveCoreAsync(claimed, expectedVersion, fencingToken, isClaim: true, ct).ConfigureAwait(false);
        return await LoadByIdAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"BuildRun '{runId}' disappeared after workflow claim.");
    }

    public Task SaveFencedAsync(
        BuildRun run,
        long expectedVersion,
        long fencingToken,
        CancellationToken ct = default)
    {
        if (fencingToken <= 0)
            throw new ArgumentOutOfRangeException(nameof(fencingToken));
        return SaveCoreAsync(run, expectedVersion, fencingToken, isClaim: false, ct);
    }

    private async Task SaveCoreAsync(
        BuildRun run,
        long expectedVersion,
        long? requiredFencingToken,
        bool isClaim,
        CancellationToken ct)
    {
        if (run.ConversationId is not { } conversationId)
            throw new InvalidOperationException("Cannot save a BuildRun without a ConversationId.");

        var gate = _gates.GetOrAdd(conversationId.Value, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = GetRunFilePath(conversationId);
            await using var fileLock = await AcquireFileLockAsync(path + ".lock", ct).ConfigureAwait(false);
            var existing = LoadCurrent(path, expectedConversationId: conversationId);
            var actualVersion = existing?.Version ?? 0;
            if (actualVersion != expectedVersion)
            {
                throw new InvalidOperationException(
                    $"Concurrency conflict: expected version {expectedVersion} but found {actualVersion}.");
            }

            if (existing is not null && existing.Id != run.Id)
            {
                throw new InvalidDataException(
                    $"BuildRun checkpoint for conversation '{conversationId}' belongs to run '{existing.Id}', not '{run.Id}'.");
            }

            ValidateFencing(existing, run, requiredFencingToken, isClaim);

            var updated = run with
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                Version = expectedVersion + 1,
            };
            var events = LoadEventSequence(GetEventFilePath(path), run.Id, conversationId);
            if (events.Count == 0 && existing is not null)
                events = [CreateEvent(null, existing)];

            var buildEvent = CreateEvent(existing, updated);
            if (events.All(item => !string.Equals(item.EventId, buildEvent.EventId, StringComparison.Ordinal)))
                events = [.. events, buildEvent];

            ValidateEventSequence(events, run.Id, conversationId);
            WriteJsonWithBackup(GetEventFilePath(path), events, ValidateEventFile);
            WriteJsonWithBackup(path, updated, ValidateCheckpointFile);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<BuildRun?> LoadByIdAsync(BuildRunId id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var directory in Directory.EnumerateDirectories(_baseDirectory))
        {
            ct.ThrowIfCancellationRequested();
            var run = LoadCurrent(Path.Combine(directory, "run.json"));
            if (run is not null && run.Id == id)
                return Task.FromResult<BuildRun?>(run);
        }

        return Task.FromResult<BuildRun?>(null);
    }

    public Task<IReadOnlyList<BuildRunEvent>> LoadEventsAsync(
        BuildRunId runId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var directory in Directory.EnumerateDirectories(_baseDirectory))
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, "run.json");
            var checkpoint = LoadCheckpoint(path);
            if (checkpoint?.Id != runId)
                continue;

            IReadOnlyList<BuildRunEvent> events = LoadEventSequence(
                GetEventFilePath(path),
                runId,
                checkpoint.ConversationId!.Value);
            return Task.FromResult(events);
        }

        return Task.FromResult<IReadOnlyList<BuildRunEvent>>([]);
    }

    public async Task<BuildRun?> ReplayAsync(BuildRunId runId, CancellationToken ct = default)
    {
        var events = await LoadEventsAsync(runId, ct).ConfigureAwait(false);
        if (events.Count == 0)
            return null;

        var conversationId = events[0].ConversationId;
        ValidateEventSequence(events, runId, conversationId);
        return events[^1].Snapshot;
    }

    private static void ValidateFencing(
        BuildRun? existing,
        BuildRun candidate,
        long? requiredFencingToken,
        bool isClaim)
    {
        if (isClaim)
        {
            if (requiredFencingToken is not { } claimToken
                || candidate.WorkflowFencingToken != claimToken)
            {
                throw new InvalidOperationException("BuildRun workflow claim has an invalid fencing token.");
            }
            if (existing?.WorkflowFencingToken is { } currentToken && claimToken <= currentToken)
                throw new InvalidOperationException("Stale BuildRun workflow fencing token.");
            return;
        }

        if (existing?.WorkflowFencingToken is { } fencedToken)
        {
            if (requiredFencingToken != fencedToken
                || candidate.WorkflowFencingToken != fencedToken)
            {
                throw new InvalidOperationException("Stale BuildRun workflow fencing token.");
            }
            return;
        }

        if (requiredFencingToken is not null || candidate.WorkflowFencingToken is not null)
            throw new InvalidOperationException("BuildRun must be claimed before fenced writes.");
    }

    private string GetRunFilePath(SessionId conversationId)
    {
        var directory = Path.Combine(_baseDirectory, conversationId.Value);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "run.json");
    }

    private static string GetEventFilePath(string checkpointPath) =>
        Path.Combine(Path.GetDirectoryName(checkpointPath)!, "events.json");

    private static BuildRun? LoadCurrent(
        string checkpointPath,
        SessionId? expectedConversationId = null)
    {
        var checkpoint = LoadCheckpoint(checkpointPath, expectedConversationId);
        if (checkpoint is null)
        {
            var recoveredFromEvents = LoadUnboundEventSequence(
                GetEventFilePath(checkpointPath),
                expectedConversationId);
            return recoveredFromEvents.Count == 0
                ? null
                : recoveredFromEvents[^1].Snapshot;
        }

        var events = LoadEventSequence(
            GetEventFilePath(checkpointPath),
            checkpoint.Id,
            checkpoint.ConversationId!.Value);
        if (events.Count == 0)
            return checkpoint;

        var replayed = events[^1].Snapshot;
        if (checkpoint.Version > replayed.Version)
        {
            throw new InvalidDataException(
                $"BuildRun checkpoint '{checkpointPath}' is ahead of its durable event sequence.");
        }

        return replayed;
    }

    private static BuildRun? LoadCheckpoint(
        string path,
        SessionId? expectedConversationId = null)
    {
        var primary = TryReadCheckpoint(path, expectedConversationId);
        if (primary is not null)
            return primary;

        var backupPath = path + ".bak";
        var backup = TryReadCheckpoint(backupPath, expectedConversationId);
        if (backup is not null)
            return backup;

        if (!File.Exists(path) && !File.Exists(backupPath))
            return null;

        throw new InvalidDataException(
            $"BuildRun checkpoint '{path}' and its backup are corrupt or incompatible.");
    }

    private static IReadOnlyList<BuildRunEvent> LoadUnboundEventSequence(
        string path,
        SessionId? expectedConversationId)
    {
        if (!File.Exists(path) && !File.Exists(path + ".bak"))
            return [];

        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate))
                continue;
            try
            {
                var events = JsonSerializer.Deserialize<IReadOnlyList<BuildRunEvent>>(
                    File.ReadAllText(candidate),
                    s_jsonOptions)
                    ?? throw new InvalidDataException($"BuildRun event sequence '{candidate}' is empty.");
                if (events.Count == 0)
                    throw new InvalidDataException($"BuildRun event sequence '{candidate}' is empty.");
                if (expectedConversationId is { } expected
                    && events[0].ConversationId != expected)
                {
                    throw new InvalidDataException(
                        $"BuildRun event sequence '{candidate}' belongs to conversation '{events[0].ConversationId}', not '{expected}'.");
                }
                ValidateEventSequence(events, events[0].RunId, events[0].ConversationId);
                return events;
            }
            catch (JsonException)
            {
            }
            catch (InvalidDataException)
            {
            }
            catch (IOException)
            {
            }
        }

        throw new InvalidDataException(
            $"BuildRun event sequence '{path}' and its backup are corrupt or incompatible.");
    }

    private static IReadOnlyList<BuildRunEvent> LoadEventSequence(
        string path,
        BuildRunId runId,
        SessionId conversationId)
    {
        if (!File.Exists(path) && !File.Exists(path + ".bak"))
            return [];

        var primary = TryReadEvents(path, runId, conversationId);
        if (primary is not null)
            return primary;

        var backup = TryReadEvents(path + ".bak", runId, conversationId);
        if (backup is not null)
            return backup;

        throw new InvalidDataException(
            $"BuildRun event sequence '{path}' and its backup are corrupt or incompatible.");
    }

    private static BuildRun? TryReadCheckpoint(
        string path,
        SessionId? expectedConversationId = null)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return ReadCheckpoint(path, expectedConversationId);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static IReadOnlyList<BuildRunEvent>? TryReadEvents(
        string path,
        BuildRunId runId,
        SessionId conversationId)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var events = JsonSerializer.Deserialize<IReadOnlyList<BuildRunEvent>>(
                File.ReadAllText(path),
                s_jsonOptions)
                ?? throw new InvalidDataException($"BuildRun event sequence '{path}' is empty.");
            ValidateEventSequence(events, runId, conversationId);
            return events;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static BuildRun ReadCheckpoint(
        string path,
        SessionId? expectedConversationId = null)
    {
        var run = JsonSerializer.Deserialize<BuildRun>(
            File.ReadAllText(path),
            s_jsonOptions)
            ?? throw new InvalidDataException($"BuildRun checkpoint '{path}' is empty.");
        ValidateRun(run, path, expectedConversationId);
        return run;
    }

    private static void ValidateRun(
        BuildRun run,
        string source,
        SessionId? expectedConversationId = null)
    {
        if (string.IsNullOrWhiteSpace(run.Id.Value)
            || run.ConversationId is not { } conversationId
            || run.Version < 0
            || run.SequenceNumber < 0)
        {
            throw new InvalidDataException($"BuildRun data '{source}' has an invalid identity or version.");
        }

        if (expectedConversationId is { } expected && conversationId != expected)
        {
            throw new InvalidDataException(
                $"BuildRun data '{source}' belongs to conversation '{conversationId}', not '{expected}'.");
        }
    }

    private static BuildRunEvent CreateEvent(BuildRun? previous, BuildRun current) =>
        new(
            $"{current.Id}:{current.Version}",
            current.Id,
            current.ConversationId!.Value,
            current.Version,
            current.SequenceNumber,
            previous?.State,
            current.State,
            current.UpdatedAt,
            current);

    private static void ValidateEventSequence(
        IReadOnlyList<BuildRunEvent> events,
        BuildRunId runId,
        SessionId conversationId)
    {
        long? previousVersion = null;
        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in events.OrderBy(item => item.Version))
        {
            ValidateRun(item.Snapshot, item.EventId, conversationId);
            if (item.RunId != runId
                || item.ConversationId != conversationId
                || item.Snapshot.Id != runId
                || item.Snapshot.Version != item.Version
                || item.Snapshot.SequenceNumber != item.SequenceNumber
                || item.Snapshot.State != item.ToState
                || !eventIds.Add(item.EventId))
            {
                throw new InvalidDataException(
                    $"BuildRun event '{item.EventId}' has inconsistent identity or snapshot data.");
            }

            if (previousVersion is { } version && item.Version != version + 1)
            {
                throw new InvalidDataException(
                    $"BuildRun event sequence for '{runId}' is not contiguous at version {item.Version}.");
            }

            previousVersion = item.Version;
        }
    }

    private static async Task<FileStream> AcquireFileLockAsync(
        string lockPath,
        CancellationToken ct)
    {
        while (true)
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
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), ct).ConfigureAwait(false);
            }
        }
    }

    private static void WriteJsonWithBackup<T>(
        string path,
        T value,
        Action<string> validator)
    {
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var json = JsonSerializer.Serialize(value, s_jsonOptions);
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            validator(tempPath);
            if (File.Exists(path))
            {
                try
                {
                    validator(path);
                    File.Copy(path, path + ".bak", overwrite: true);
                }
                catch (InvalidDataException)
                {
                    // Preserve the previous valid backup when the primary is already corrupt.
                }
                catch (JsonException)
                {
                    // Preserve the previous valid backup when the primary is already corrupt.
                }
            }

            File.Move(tempPath, path, overwrite: true);
            validator(path);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static void ValidateCheckpointFile(string path) =>
        _ = ReadCheckpoint(path);

    private static void ValidateEventFile(string path)
    {
        var events = JsonSerializer.Deserialize<IReadOnlyList<BuildRunEvent>>(
            File.ReadAllText(path),
            s_jsonOptions)
            ?? throw new InvalidDataException($"BuildRun event sequence '{path}' is empty.");
        if (events.Count == 0)
            throw new InvalidDataException($"BuildRun event sequence '{path}' is empty.");
        ValidateEventSequence(events, events[0].RunId, events[0].ConversationId);
    }
}
