using OneCode.Core.Goals;

namespace OneCode.Infrastructure.Goals;

public sealed class JsonGoalRunStore : IGoalRunStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _baseDirectory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public JsonGoalRunStore(string? basePath = null)
    {
        _baseDirectory = basePath ?? Path.Combine(PathsHelper.GetUserConfigDir(), "goal-runs");
        Directory.CreateDirectory(_baseDirectory);
    }

    public Task<GoalRun?> LoadBySessionAsync(
        Core.Domain.SessionId sessionId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(LoadCurrent(GetRunFilePath(sessionId)));
    }

    public Task<GoalRun?> LoadByIdAsync(GoalRunId runId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var directory in Directory.EnumerateDirectories(_baseDirectory))
        {
            ct.ThrowIfCancellationRequested();
            var current = LoadCurrent(Path.Combine(directory, "run.json"));
            if (current?.Id == runId)
                return Task.FromResult<GoalRun?>(current);
        }
        return Task.FromResult<GoalRun?>(null);
    }

    public Task SaveAsync(GoalRun run, long expectedVersion, CancellationToken ct = default)
        => SaveCoreAsync(run, expectedVersion, requiredFencingToken: null, isClaim: false, ct);

    public async Task<GoalRun> ClaimWorkflowAsync(
        GoalRunId runId,
        long fencingToken,
        long expectedVersion,
        CancellationToken ct = default)
    {
        if (fencingToken <= 0)
            throw new ArgumentOutOfRangeException(nameof(fencingToken));
        var current = await LoadByIdAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"GoalRun '{runId}' was not found.");
        var claimed = current with { WorkflowFencingToken = fencingToken };
        await SaveCoreAsync(claimed, expectedVersion, fencingToken, isClaim: true, ct).ConfigureAwait(false);
        return await LoadByIdAsync(runId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"GoalRun '{runId}' disappeared after workflow claim.");
    }

    public Task SaveFencedAsync(
        GoalRun run,
        long expectedVersion,
        long fencingToken,
        CancellationToken ct = default)
    {
        if (fencingToken <= 0)
            throw new ArgumentOutOfRangeException(nameof(fencingToken));
        return SaveCoreAsync(run, expectedVersion, fencingToken, isClaim: false, ct);
    }

    public Task<IReadOnlyList<GoalRun>> ListActiveAsync(CancellationToken ct = default)
    {
        var runs = new List<GoalRun>();
        foreach (var directory in Directory.EnumerateDirectories(_baseDirectory))
        {
            ct.ThrowIfCancellationRequested();
            var current = LoadCurrent(Path.Combine(directory, "run.json"));
            if (current is not null && !current.IsTerminal)
                runs.Add(current);
        }
        return Task.FromResult<IReadOnlyList<GoalRun>>(
            runs.OrderByDescending(run => run.UpdatedAt).ToArray());
    }

    private async Task SaveCoreAsync(
        GoalRun run,
        long expectedVersion,
        long? requiredFencingToken,
        bool isClaim,
        CancellationToken ct)
    {
        ValidateAggregate(run);
        var gate = _gates.GetOrAdd(run.SessionId.Value, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = GetRunFilePath(run.SessionId);
            await using var fileLock = await AcquireFileLockAsync(path + ".lock", ct).ConfigureAwait(false);
            var current = LoadCurrent(path);
            var actualVersion = current?.Version ?? 0;
            if (actualVersion != expectedVersion)
            {
                throw new InvalidOperationException(
                    $"Concurrency conflict: expected version {expectedVersion} but found {actualVersion}.");
            }
            if (current is not null && current.Id != run.Id)
                throw new InvalidDataException($"Goal session '{run.SessionId}' already belongs to run '{current.Id}'.");

            ValidateTransition(current, run);
            ValidateFencing(current, run, requiredFencingToken, isClaim);
            var now = DateTimeOffset.UtcNow;
            var updated = run with
            {
                Version = expectedVersion + 1,
                SequenceNumber = (current?.SequenceNumber ?? 0) + 1,
                UpdatedAt = now,
                CompletedAt = run.IsTerminal ? run.CompletedAt ?? now : null,
            };
            ValidateAggregate(updated);
            WriteAtomic(path, updated);
        }
        finally
        {
            gate.Release();
        }
    }

    private static void ValidateTransition(GoalRun? current, GoalRun candidate)
    {
        if (current is null)
            return;
        if (current.DefinitionHash != candidate.DefinitionHash)
            throw new InvalidOperationException("GoalRun definition hash cannot change.");
        if (current.IsTerminal && candidate != current)
            throw new InvalidOperationException($"Terminal GoalRun '{current.Id}' is immutable.");
        if (candidate.SequenceNumber < current.SequenceNumber)
            throw new InvalidOperationException("GoalRun sequence number cannot move backwards.");
    }

    private static void ValidateFencing(
        GoalRun? current,
        GoalRun candidate,
        long? requiredFencingToken,
        bool isClaim)
    {
        if (isClaim)
        {
            if (requiredFencingToken is not { } claimToken || candidate.WorkflowFencingToken != claimToken)
                throw new InvalidOperationException("GoalRun workflow claim has an invalid fencing token.");
            if (current?.WorkflowFencingToken is { } existing && claimToken <= existing)
                throw new InvalidOperationException("Stale GoalRun workflow fencing token.");
            return;
        }
        if (current?.WorkflowFencingToken is { } token)
        {
            if (requiredFencingToken != token || candidate.WorkflowFencingToken != token)
                throw new InvalidOperationException("Stale GoalRun workflow fencing token.");
            return;
        }
        if (requiredFencingToken is not null || candidate.WorkflowFencingToken is not null)
            throw new InvalidOperationException("GoalRun must be claimed before fenced writes.");
    }

    private static void ValidateAggregate(GoalRun run)
    {
        if (string.IsNullOrWhiteSpace(run.Goal) || string.IsNullOrWhiteSpace(run.WorkingDirectory)
            || string.IsNullOrWhiteSpace(run.WorkspaceFingerprint) || string.IsNullOrWhiteSpace(run.DefinitionHash))
            throw new InvalidDataException("GoalRun identity and workspace fields are required.");
        if (run.Executions.Any(execution => run.Plan.All(step => step.Id != execution.GoalId)))
            throw new InvalidDataException("GoalRun execution evidence refers to an unknown plan step.");
        if (run.State == GoalRunState.Completed)
        {
            if (run.PublishReceipt is null)
                throw new InvalidDataException("Completed GoalRun requires a publish receipt.");
            if (run.FinalValidation.Count == 0 || run.FinalValidation.Any(gate => !gate.Passed && !gate.Skipped))
                throw new InvalidDataException("Completed GoalRun requires successful final validation evidence.");
            if (run.Plan.Any(step => !step.Optional && step.State != GoalStepState.Completed))
                throw new InvalidDataException("Completed GoalRun contains an incomplete required step.");
        }
    }

    private string GetRunFilePath(Core.Domain.SessionId sessionId)
    {
        var directory = Path.Combine(_baseDirectory, sessionId.Value);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "run.json");
    }

    private static GoalRun? LoadCurrent(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<GoalRun>(File.ReadAllText(path), s_jsonOptions)
                ?? throw new InvalidDataException($"GoalRun file '{path}' is empty.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new InvalidDataException($"GoalRun file '{path}' is invalid.", ex);
        }
    }

    private static void WriteAtomic(string path, GoalRun run)
    {
        var temporary = path + ".tmp";
        var backup = path + ".bak";
        var json = JsonSerializer.Serialize(run, s_jsonOptions);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        if (File.Exists(path))
            File.Replace(temporary, path, backup, ignoreMetadataErrors: true);
        else
            File.Move(temporary, path);
        _ = LoadCurrent(path);
    }

    private static async Task<FileStream> AcquireFileLockAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 49)
            {
                await Task.Delay(20, ct).ConfigureAwait(false);
            }
        }
        throw new IOException($"Could not acquire GoalRun lock '{path}'.");
    }
}
