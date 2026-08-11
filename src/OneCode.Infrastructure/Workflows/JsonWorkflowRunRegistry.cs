using System.Security.Cryptography;
using OneCode.Core.Workflows;

namespace OneCode.Infrastructure.Workflows;

/// <summary>
/// File-backed workflow runtime registry. Each run has one durable record and one OS-level
/// exclusive lease file. Business aggregates remain authoritative for product state.
/// </summary>
public sealed class JsonWorkflowRunRegistry : IWorkflowRunRegistry
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _root;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public JsonWorkflowRunRegistry(string? basePath = null)
    {
        _root = basePath ?? Path.Combine(PathsHelper.GetUserConfigDir(), "workflow-runs");
        Directory.CreateDirectory(_root);
    }

    public Task<WorkflowRunRecord?> LoadAsync(string runId, CancellationToken ct = default)
    {
        ValidateRunId(runId);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(LoadRecord(runId));
    }

    public Task<IReadOnlyList<WorkflowRunRecord>> LoadActiveAsync(CancellationToken ct = default)
    {
        List<WorkflowRunRecord> active = [];
        foreach (var path in Directory.EnumerateFiles(_root, "*.run.json"))
        {
            ct.ThrowIfCancellationRequested();
            var record = LoadEnvelope(path)?.Record
                ?? throw new InvalidDataException($"Workflow run record '{path}' is missing.");
            if (!record.IsTerminal)
                active.Add(record);
        }

        return Task.FromResult<IReadOnlyList<WorkflowRunRecord>>(
            active.OrderBy(record => record.CreatedAt).ThenBy(record => record.RunId, StringComparer.Ordinal).ToArray());
    }

    public async Task<IWorkflowRunLease?> TryAcquireAsync(
        WorkflowRunRegistration registration,
        CancellationToken ct = default)
    {
        ValidateRegistration(registration);
        Directory.CreateDirectory(_root);
        FileStream leaseStream;
        try
        {
            leaseStream = new FileStream(
                GetLeasePath(registration.RunId),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
        }
        catch (IOException)
        {
            return null;
        }

        try
        {
            var gate = GetGate(registration.RunId);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var current = LoadRecord(registration.RunId);
                if (current is not null)
                {
                    ValidateDefinition(current, registration);
                    if (current.IsTerminal)
                    {
                        throw new InvalidOperationException(
                            $"Workflow run '{registration.RunId}' is terminal and cannot be acquired.");
                    }
                }

                var now = DateTimeOffset.UtcNow;
                var next = current is null
                    ? new WorkflowRunRecord(
                        registration.RunId,
                        registration.RunKind,
                        registration.DefinitionHash,
                        FencingToken: 1,
                        WorkflowRunState.Active,
                        CheckpointId: null,
                        Version: 1,
                        CreatedAt: now,
                        UpdatedAt: now)
                    : current with
                    {
                        FencingToken = current.FencingToken + 1,
                        State = WorkflowRunState.Active,
                        Version = current.Version + 1,
                        UpdatedAt = now,
                    };
                await SaveRecordAsync(next, ct).ConfigureAwait(false);
                return new WorkflowRunLease(registration.RunId, next.FencingToken, leaseStream);
            }
            finally
            {
                gate.Release();
            }
        }
        catch
        {
            await leaseStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<WorkflowRunRecord> BeginGenerationAsync(
        string runId,
        long fencingToken,
        int generation,
        CancellationToken ct = default)
    {
        if (generation <= 0)
            throw new ArgumentOutOfRangeException(nameof(generation));
        return UpdateAsync(runId, fencingToken, record =>
        {
            if (record.ExecutionGeneration > generation)
                throw new InvalidOperationException("Stale workflow execution generation.");
            if (record.ExecutionGeneration == generation)
                return record;
            return record with
            {
                ExecutionGeneration = generation,
                CheckpointId = null,
                PendingRequest = null,
                Version = record.Version + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }, ct);
    }

    public async Task ReconcileCheckpointAsync(
        string runId,
        long fencingToken,
        string checkpointId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(checkpointId))
            throw new ArgumentException("CheckpointId is required.", nameof(checkpointId));
        _ = await UpdateAsync(runId, fencingToken, record => record with
        {
            CheckpointId = checkpointId,
            Version = record.Version + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);
    }

    public async Task RegisterPendingRequestAsync(
        string runId,
        long fencingToken,
        WorkflowPendingRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RequestId)
            || string.IsNullOrWhiteSpace(request.PortId)
            || string.IsNullOrWhiteSpace(request.CommandId))
        {
            throw new ArgumentException("Pending request identity is incomplete.", nameof(request));
        }

        _ = await UpdateAsync(runId, fencingToken, record =>
        {
            if (record.PendingRequest is { } existing)
            {
                if (!HasSameRequestIdentity(existing, request))
                    throw new InvalidOperationException($"Workflow run '{runId}' already has another pending request.");
                return record;
            }
            return record with
                {
                    PendingRequest = request,
                    Version = record.Version + 1,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
        }, ct).ConfigureAwait(false);
    }

    public async Task ConsumePendingRequestAsync(
        string runId,
        long fencingToken,
        WorkflowPendingRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = await UpdateAsync(runId, fencingToken, record =>
        {
            var pending = record.PendingRequest
                ?? throw new InvalidOperationException($"Workflow run '{runId}' has no pending request.");
            if (!HasSameRequestIdentity(pending, request))
                throw new InvalidOperationException("Pending workflow request identity does not match.");
            if (pending.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("Pending workflow request has expired.");
            return record with
            {
                PendingRequest = null,
                Version = record.Version + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }, ct).ConfigureAwait(false);
    }

    public async Task CompleteAsync(
        string runId,
        long fencingToken,
        WorkflowRunState terminalState,
        CancellationToken ct = default)
    {
        if (terminalState is WorkflowRunState.Active)
            throw new ArgumentOutOfRangeException(nameof(terminalState), "A terminal workflow state is required.");
        _ = await UpdateAsync(runId, fencingToken, record =>
        {
            if (record.IsTerminal)
            {
                if (record.State != terminalState)
                {
                    throw new InvalidOperationException(
                        $"Workflow run '{runId}' is already terminal as '{record.State}'.");
                }
                return record;
            }

            var now = DateTimeOffset.UtcNow;
            return record with
            {
                State = terminalState,
                Version = record.Version + 1,
                UpdatedAt = now,
                CompletedAt = now,
            };
        }, ct).ConfigureAwait(false);
    }

    private async Task<WorkflowRunRecord> UpdateAsync(
        string runId,
        long fencingToken,
        Func<WorkflowRunRecord, WorkflowRunRecord> update,
        CancellationToken ct)
    {
        ValidateRunId(runId);
        if (fencingToken <= 0)
            throw new ArgumentOutOfRangeException(nameof(fencingToken));

        var gate = GetGate(runId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = LoadRecord(runId)
                ?? throw new InvalidOperationException($"Workflow run '{runId}' does not exist.");
            if (current.FencingToken != fencingToken)
                throw new InvalidOperationException("Stale workflow fencing token.");
            if (current.State != WorkflowRunState.Active && !current.IsTerminal)
                throw new InvalidOperationException($"Workflow run '{runId}' is not active.");

            var next = update(current);
            if (!ReferenceEquals(next, current) && next != current)
                await SaveRecordAsync(next, ct).ConfigureAwait(false);
            return next;
        }
        finally
        {
            gate.Release();
        }
    }

    private WorkflowRunRecord? LoadRecord(string runId)
    {
        var path = GetRecordPath(runId);
        var envelope = LoadEnvelope(path) ?? LoadEnvelope(path + ".bak");
        if (envelope is null)
        {
            if (!File.Exists(path) && !File.Exists(path + ".bak"))
                return null;
            throw new InvalidDataException($"Workflow run record '{path}' and its backup are corrupt.");
        }
        if (!string.Equals(envelope.Record.RunId, runId, StringComparison.Ordinal))
            throw new InvalidDataException($"Workflow run record '{path}' belongs to another run.");
        return envelope.Record;
    }

    private static WorkflowRunEnvelope? LoadEnvelope(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var envelope = JsonSerializer.Deserialize<WorkflowRunEnvelope>(File.ReadAllText(path), s_jsonOptions);
            if (envelope is null || envelope.SchemaVersion != SchemaVersion)
                return null;
            var payload = JsonSerializer.Serialize(envelope.Record, s_jsonOptions);
            return string.Equals(envelope.Checksum, ComputeChecksum(payload), StringComparison.Ordinal)
                ? envelope
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task SaveRecordAsync(WorkflowRunRecord record, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(record, s_jsonOptions);
        var envelope = new WorkflowRunEnvelope(SchemaVersion, ComputeChecksum(payload), record);
        var content = JsonSerializer.Serialize(envelope, s_jsonOptions);
        var path = GetRecordPath(record.RunId);
        var temporaryPath = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                await writer.WriteAsync(content.AsMemory(), ct).ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
                File.Replace(temporaryPath, path, path + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private SemaphoreSlim GetGate(string runId) => _gates.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
    private string GetRecordPath(string runId) => Path.Combine(_root, SafeName(runId) + ".run.json");
    private string GetLeasePath(string runId) => Path.Combine(_root, SafeName(runId) + ".lease");

    private static string SafeName(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string ComputeChecksum(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void ValidateRegistration(WorkflowRunRegistration registration)
    {
        ValidateRunId(registration.RunId);
        if (string.IsNullOrWhiteSpace(registration.RunKind))
            throw new ArgumentException("RunKind is required.", nameof(registration));
        if (string.IsNullOrWhiteSpace(registration.DefinitionHash))
            throw new ArgumentException("DefinitionHash is required.", nameof(registration));
    }

    private static void ValidateRunId(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("RunId is required.", nameof(runId));
    }

    private static bool HasSameRequestIdentity(
        WorkflowPendingRequest left,
        WorkflowPendingRequest right)
        => string.Equals(left.RequestId, right.RequestId, StringComparison.Ordinal)
            && string.Equals(left.PortId, right.PortId, StringComparison.Ordinal)
            && string.Equals(left.CommandId, right.CommandId, StringComparison.Ordinal);

    private static void ValidateDefinition(WorkflowRunRecord current, WorkflowRunRegistration registration)
    {
        if (!string.Equals(current.RunKind, registration.RunKind, StringComparison.Ordinal)
            || !string.Equals(current.DefinitionHash, registration.DefinitionHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Workflow run '{current.RunId}' definition does not match its durable registration.");
        }
    }

    private sealed record WorkflowRunEnvelope(int SchemaVersion, string Checksum, WorkflowRunRecord Record);

    private sealed class WorkflowRunLease(string runId, long fencingToken, FileStream stream) : IWorkflowRunLease
    {
        public string RunId { get; } = runId;
        public long FencingToken { get; } = fencingToken;
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
