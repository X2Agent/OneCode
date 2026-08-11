using System.Security.Cryptography;
using OneCode.Core.Workflows;

namespace OneCode.Infrastructure.Workflows;

/// <summary>
/// File-backed Operation Ledger (S-04). One envelope-per-operation JSON file with an SHA-256
/// checksum and atomic replace, mirroring <see cref="JsonWorkflowRunRegistry"/> durability rules:
/// temporary file + <c>File.Replace</c> + <c>.bak</c>, per-operation in-process gate, and a
/// fencing-token check on every mutation. BeforeContent is stored base64 so crash recovery can
/// restore exact pre-write bytes.
/// </summary>
public sealed class FileOperationLedger : IOperationLedger
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

    public FileOperationLedger(string? basePath = null)
    {
        _root = basePath ?? Path.Combine(PathsHelper.GetUserConfigDir(), "operation-ledger");
        Directory.CreateDirectory(_root);
    }

    public Task<OperationTransaction?> LoadAsync(string operationId, CancellationToken ct = default)
    {
        ValidateOperationId(operationId);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(LoadEnvelope(GetPath(operationId))?.Transaction);
    }

    public Task<OperationTransaction> BeginTransactionAsync(
        string operationId,
        string operationKind,
        long fencingToken,
        CancellationToken ct = default)
    {
        ValidateOperationId(operationId);
        if (string.IsNullOrWhiteSpace(operationKind))
            throw new ArgumentException("OperationKind is required.", nameof(operationKind));
        if (fencingToken <= 0)
            throw new ArgumentOutOfRangeException(nameof(fencingToken));

        return UpdateAsync(operationId, fencingToken, current =>
        {
            if (current is not null)
            {
                if (!string.Equals(current.OperationKind, operationKind, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Operation '{operationId}' already exists with kind '{current.OperationKind}'.");
                }

                return current;
            }

            var now = DateTimeOffset.UtcNow;
            return new OperationTransaction(
                operationId,
                operationKind,
                fencingToken,
                OperationTransactionState.Active,
                [],
                Evidence: null,
                now);
        }, ct);
    }

    public Task AddFileIntentAsync(
        string operationId,
        long fencingToken,
        string path,
        byte[]? beforeContent,
        CancellationToken ct = default)
    {
        ValidateOperationId(operationId);
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));

        return UpdateAsync(operationId, fencingToken, current =>
        {
            var active = RequireActive(current, operationId);
            var normalized = Path.GetFullPath(path);
            var existing = active.FileIntents.FirstOrDefault(intent =>
                string.Equals(intent.Path, normalized, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                return active;

            var beforeHash = beforeContent is { Length: > 0 }
                ? Convert.ToHexString(SHA256.HashData(beforeContent)).ToLowerInvariant()
                : null;
            return active with
            {
                FileIntents = [.. active.FileIntents, new FileIntent(normalized, beforeContent, beforeHash)],
            };
        }, ct);
    }

    public Task CommitTransactionAsync(
        string operationId,
        long fencingToken,
        string? evidence,
        CancellationToken ct = default)
    {
        ValidateOperationId(operationId);

        return UpdateAsync(operationId, fencingToken, current =>
        {
            var active = RequireActive(current, operationId);
            if (active.IsCommitted)
                return active;

            var committed = active with
            {
                State = OperationTransactionState.Committed,
                Evidence = evidence,
                CommittedAt = DateTimeOffset.UtcNow,
                // Fill AfterHash at commit time so recovery can also cross-check current content.
                FileIntents = active.FileIntents
                    .Select(intent => intent with { AfterHash = ComputeFileHash(intent.Path) })
                    .ToArray(),
            };
            return committed;
        }, ct);
    }

    public async Task<TransactionRollbackResult> ReconcileAndRollbackAsync(
        string operationId,
        CancellationToken ct = default)
    {
        ValidateOperationId(operationId);
        ct.ThrowIfCancellationRequested();

        var gate = GetGate(operationId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = LoadEnvelope(GetPath(operationId))?.Transaction;
            if (current is null || current.IsCommitted)
                return new TransactionRollbackResult(operationId, [], [], current?.IsCommitted ?? false);

            List<string> rolledBack = [];
            List<string> failed = [];
            foreach (var intent in current.FileIntents)
            {
                try
                {
                    if (intent.BeforeContent is null)
                    {
                        // File did not exist before the transaction: remove the residual.
                        if (File.Exists(intent.Path))
                            File.Delete(intent.Path);
                    }
                    else
                    {
                        File.WriteAllBytes(intent.Path, intent.BeforeContent);
                    }

                    rolledBack.Add(intent.Path);
                }
                catch (Exception)
                {
                    failed.Add(intent.Path);
                }
            }

            return new TransactionRollbackResult(operationId, rolledBack, failed, AlreadyCommitted: false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<TransactionRollbackResult>> ReconcileRunAsync(
        string runIdPrefix,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runIdPrefix))
            throw new ArgumentException("RunId prefix is required.", nameof(runIdPrefix));
        ct.ThrowIfCancellationRequested();

        var results = new List<TransactionRollbackResult>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.op.json"))
        {
            ct.ThrowIfCancellationRequested();
            var envelope = LoadEnvelope(path);
            if (envelope?.Transaction is not { IsCommitted: false } transaction
                || !transaction.OperationId.StartsWith(runIdPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            results.Add(await ReconcileAndRollbackAsync(transaction.OperationId, ct).ConfigureAwait(false));
        }

        return results;
    }

    private static OperationTransaction RequireActive(OperationTransaction? current, string operationId)
        => current ?? throw new InvalidOperationException(
            $"Operation '{operationId}' has no ledger record; begin the transaction first.");

    private Task<OperationTransaction> UpdateAsync(
        string operationId,
        long fencingToken,
        Func<OperationTransaction?, OperationTransaction> update,
        CancellationToken ct)
    {
        var gate = GetGate(operationId);
        return ExecuteWithGateAsync(gate, ct, async () =>
        {
            var current = LoadEnvelope(GetPath(operationId))?.Transaction;
            if (current is not null && current.FencingToken != fencingToken)
                throw new InvalidOperationException($"Stale Operation Ledger fencing token for '{operationId}'.");

            var next = update(current);
            if (next is null || ReferenceEquals(next, current))
                return current!;

            await SaveAsync(GetPath(operationId), next, ct).ConfigureAwait(false);
            return next;
        });
    }

    private static async Task<T> ExecuteWithGateAsync<T>(
        SemaphoreSlim gate,
        CancellationToken ct,
        Func<Task<T>> action)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private OperationTransactionEnvelope? LoadEnvelope(string path)
        => LoadEnvelopeCore(path) ?? LoadEnvelopeCore(path + ".bak");

    private static OperationTransactionEnvelope? LoadEnvelopeCore(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var envelope = JsonSerializer.Deserialize<OperationTransactionEnvelope>(File.ReadAllText(path), s_jsonOptions);
            if (envelope is null || envelope.SchemaVersion != SchemaVersion)
                return null;
            var payload = JsonSerializer.Serialize(envelope.Transaction, s_jsonOptions);
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

    private async Task SaveAsync(string path, OperationTransaction transaction, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(transaction, s_jsonOptions);
        var envelope = new OperationTransactionEnvelope(SchemaVersion, ComputeChecksum(payload), transaction);
        var content = JsonSerializer.Serialize(envelope, s_jsonOptions);
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

    private SemaphoreSlim GetGate(string operationId) => _gates.GetOrAdd(operationId, _ => new SemaphoreSlim(1, 1));
    private string GetPath(string operationId) => Path.Combine(_root, SafeName(operationId) + ".op.json");

    private static string SafeName(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string ComputeChecksum(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string? ComputeFileHash(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void ValidateOperationId(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("OperationId is required.", nameof(operationId));
    }

    private sealed record OperationTransactionEnvelope(int SchemaVersion, string Checksum, OperationTransaction Transaction);
}
