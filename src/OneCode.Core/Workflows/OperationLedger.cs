namespace OneCode.Core.Workflows;

/// <summary>Lifecycle state of a durable file transaction recorded in the Operation Ledger.</summary>
public enum OperationTransactionState
{
    /// <summary>Transaction started; file intents are being (or were) recorded. A crash here leaves a rollback window.</summary>
    Active,

    /// <summary>Transaction durably committed; recovery must not roll back its files.</summary>
    Committed,
}

/// <summary>
/// A single file side-effect intent inside a durable transaction. BeforeContent is the pre-write
/// file content (or null for a newly created file) and is the rollback basis after a crash —
/// content, not just a hash, so recovery can restore the exact previous bytes.
/// </summary>
public sealed record FileIntent(
    string Path,
    byte[]? BeforeContent,
    string? BeforeHash)
{
    /// <summary>Content hash observed after the write (filled at commit time).</summary>
    public string? AfterHash { get; init; }
}

/// <summary>
/// Durable record of one file transaction. Written by <see cref="IOperationLedger"/> with an
/// atomic receipt so a crash between a side effect and its commit can be reconciled on resume.
/// </summary>
public sealed record OperationTransaction(
    string OperationId,
    string OperationKind,
    long FencingToken,
    OperationTransactionState State,
    IReadOnlyList<FileIntent> FileIntents,
    string? Evidence,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CommittedAt = null)
{
    public bool IsCommitted => State == OperationTransactionState.Committed;
}

/// <summary>Result of reconciling an unfinished transaction after a crash.</summary>
public sealed record TransactionRollbackResult(
    string OperationId,
    IReadOnlyList<string> RolledBackFiles,
    IReadOnlyList<string> FailedRollbacks,
    bool AlreadyCommitted)
{
    public bool HadResidual => RolledBackFiles.Count > 0 || FailedRollbacks.Count > 0;
}

/// <summary>
/// Durable Operation Ledger (S-04). Persists file-transaction receipts so that a crash between a
/// side effect and its MAF checkpoint never silently duplicates or orphans irreversible writes:
/// unfinished transactions are rolled back on resume from their recorded BeforeContent.
/// </summary>
public interface IOperationLedger
{
    Task<OperationTransaction?> LoadAsync(string operationId, CancellationToken ct = default);

    /// <summary>Starts a durable file transaction. Idempotent per (operationId, fencingToken).</summary>
    Task<OperationTransaction> BeginTransactionAsync(
        string operationId,
        string operationKind,
        long fencingToken,
        CancellationToken ct = default);

    /// <summary>
    /// Records the pre-write intent for one file. Must be persisted before the write happens so the
    /// crash window "write happened but receipt did not" is eliminated for rollback purposes.
    /// </summary>
    Task AddFileIntentAsync(
        string operationId,
        long fencingToken,
        string path,
        byte[]? beforeContent,
        CancellationToken ct = default);

    /// <summary>Marks the transaction durably committed. Recovery will not roll back its files.</summary>
    Task CommitTransactionAsync(
        string operationId,
        long fencingToken,
        string? evidence,
        CancellationToken ct = default);

    /// <summary>
    /// Reconcilies an unfinished transaction after a crash: restores each recorded file to its
    /// BeforeContent (deletes files that did not exist before) unless the transaction committed.
    /// </summary>
    Task<TransactionRollbackResult> ReconcileAndRollbackAsync(
        string operationId,
        CancellationToken ct = default);

    /// <summary>
    /// Reconcilies every unfinished transaction whose OperationId starts with <paramref name="runIdPrefix"/>.
    /// Used at the start of a new execution generation: attempt-scoped OperationIds differ across
    /// generations, so this sweep rolls back residuals left by a crashed previous generation.
    /// </summary>
    Task<IReadOnlyList<TransactionRollbackResult>> ReconcileRunAsync(
        string runIdPrefix,
        CancellationToken ct = default);
}
