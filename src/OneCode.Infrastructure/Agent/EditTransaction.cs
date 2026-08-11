namespace OneCode.Infrastructure.Agent;

/// <summary>
/// Snapshot-based edit transaction for file-modifying tools.
/// If the transaction is disposed without Commit(), snapshotted files are restored.
/// </summary>
public sealed class EditTransaction : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _newFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _lastTouchedVersion = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _expectedCurrentHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<EditTransaction>? _logger;
    private long _changeVersion;
    private bool _committed;
    private bool _rollbackOnDispose = true;
    private TransactionPersistenceContext? _persistence;

    public EditTransaction(ILogger<EditTransaction>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Durable Operation Ledger binding (S-04). When set, the edit pipeline records every file
    /// intent (path + pre-write content) into the ledger before the write; a crash mid-transaction
    /// can then be rolled back from the persisted receipt on resume.
    /// </summary>
    public TransactionPersistenceContext? Persistence => _persistence;

    /// <summary>Binds this transaction to a durable Operation Ledger receipt (S-04).</summary>
    public void PersistTo(OneCode.Core.Workflows.IOperationLedger ledger, string operationId, long fencingToken)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("OperationId is required.", nameof(operationId));
        if (fencingToken <= 0)
            throw new ArgumentOutOfRangeException(nameof(fencingToken));
        _persistence = new TransactionPersistenceContext(ledger, operationId, fencingToken);
    }

    public int SnapshotCount
    {
        get
        {
            lock (_gate)
                return _snapshots.Count;
        }
    }

    /// <summary>
    /// Returns the list of files snapshotted (i.e. modified) in this transaction.
    /// Used by final validation to determine which files to verify.
    /// </summary>
    public IReadOnlyList<string> GetModifiedFiles()
    {
        lock (_gate)
            return _snapshots.Keys.ToList();
    }

    /// <summary>
    /// Captures the current transaction change version. Callers can later use
    /// <see cref="GetModifiedFilesSince"/> to attribute every write, including rewrites
    /// of files touched by earlier sub-goals, to the current sub-goal.
    /// </summary>
    public long CaptureChangeVersion()
    {
        lock (_gate)
            return _changeVersion;
    }

    /// <summary>Returns files modified after the supplied change version.</summary>
    public IReadOnlyList<string> GetModifiedFilesSince(long version)
    {
        lock (_gate)
            return _lastTouchedVersion
                .Where(entry => entry.Value > version)
                .Select(entry => entry.Key)
                .ToList();
    }

    /// <summary>
    /// Captures hashes of the transaction's current file contents immediately before validation.
    /// A later mismatch indicates a write occurred outside the transaction validation window.
    /// </summary>
    public void CaptureValidationBaseline()
    {
        lock (_gate)
        {
            _expectedCurrentHashes.Clear();
            foreach (var path in _snapshots.Keys)
                _expectedCurrentHashes[path] = ComputeCurrentHash(path);
        }
    }

    /// <summary>Returns files whose contents changed after <see cref="CaptureValidationBaseline"/>.</summary>
    public IReadOnlyList<string> GetValidationConflicts()
    {
        lock (_gate)
            return _expectedCurrentHashes
                .Where(entry => !string.Equals(entry.Value, ComputeCurrentHash(entry.Key), StringComparison.Ordinal))
                .Select(entry => entry.Key)
                .ToList();
    }

    /// <summary>Whether the transaction has been committed.</summary>
    public bool IsCommitted
    {
        get
        {
            lock (_gate)
                return _committed;
        }
    }

    public void Snapshot(string path)
    {
        lock (_gate)
        {
            if (_snapshots.ContainsKey(path))
            {
                _lastTouchedVersion[path] = ++_changeVersion;
                return;
            }

            if (File.Exists(path))
            {
                // Guard against OOM on large files
                var fileSize = new FileInfo(path).Length;
                if (fileSize > PathsHelper.MaxFileReadSize)
                {
                    _logger?.LogWarning("EditTransaction: refusing to snapshot '{Path}' ({SizeMB}MB exceeds {MaxMB}MB limit)",
                        path, fileSize / 1024 / 1024, PathsHelper.MaxFileReadSize / 1024 / 1024);
                    throw new InvalidOperationException(
                        $"File '{path}' is too large to snapshot ({fileSize / 1024 / 1024}MB). " +
                        $"Maximum supported size is {PathsHelper.MaxFileReadSize / 1024 / 1024}MB.");
                }

                _snapshots[path] = File.ReadAllBytes(path);
                _logger?.LogDebug("EditTransaction: snapshotted '{Path}' ({Bytes} bytes)", path, _snapshots[path].Length);
            }
            else
            {
                _snapshots[path] = [];
                _newFiles.Add(path);
                _logger?.LogDebug("EditTransaction: snapshotted new file '{Path}'", path);
            }

            _lastTouchedVersion[path] = ++_changeVersion;
        }
    }

    /// <summary>
    /// Abandons automatic rollback while preserving the current files.
    /// This is only for conflict paths where restoring stale snapshots would overwrite
    /// external changes. It is not a successful commit.
    /// </summary>
    public void PreserveForManualReconciliation()
    {
        lock (_gate)
        {
            _rollbackOnDispose = false;
            _logger?.LogWarning(
                "EditTransaction preserving {Count} files for manual conflict reconciliation",
                _snapshots.Count);
        }
    }

    public void Commit()
    {
        lock (_gate)
        {
            var conflicts = _expectedCurrentHashes
                .Where(entry => !string.Equals(entry.Value, ComputeCurrentHash(entry.Key), StringComparison.Ordinal))
                .Select(entry => entry.Key)
                .ToList();
            if (conflicts.Count > 0)
            {
                throw new InvalidOperationException(
                    $"EditTransaction commit conflict: {string.Join(", ", conflicts)} changed after final validation.");
            }

            _committed = true;
            _logger?.LogInformation("EditTransaction committed: {Count} files", _snapshots.Count);
            _snapshots.Clear();
            _newFiles.Clear();
            _lastTouchedVersion.Clear();
            _expectedCurrentHashes.Clear();
        }
    }

    public void Rollback()
    {
        lock (_gate)
        {
            if (_snapshots.Count == 0) return;

            _logger?.LogWarning("EditTransaction rolling back {Count} files", _snapshots.Count);
            var errors = 0;

            foreach (var (path, original) in _snapshots)
            {
                try
                {
                    if (_newFiles.Contains(path))
                    {
                        if (File.Exists(path)) File.Delete(path);
                        _logger?.LogDebug("Rollback: deleted '{Path}'", path);
                    }
                    else
                    {
                        File.WriteAllBytes(path, original);
                        _logger?.LogDebug("Rollback: restored '{Path}' ({Bytes} bytes)", path, original.Length);
                    }
                }
                catch (Exception ex)
                {
                    errors++;
                    _logger?.LogError(ex, "Rollback failed for '{Path}'", path);
                }
            }

            _snapshots.Clear();
            _newFiles.Clear();
            _lastTouchedVersion.Clear();
            _expectedCurrentHashes.Clear();

            if (errors > 0)
                _logger?.LogError("Rollback completed with {Errors} errors", errors);
        }
    }

    private static string? ComputeCurrentHash(string path)
    {
        if (!File.Exists(path))
            return null;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    void IDisposable.Dispose()
    {
        lock (_gate)
        {
            if (_committed || !_rollbackOnDispose) return;
        }

        Rollback();
    }
}
