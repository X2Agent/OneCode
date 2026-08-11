using OneCode.Core.Workflows;

namespace OneCode.Infrastructure.Agent;

/// <summary>
/// Carries the durable Operation Ledger binding for one agent run into the pipeline. When enabled,
/// <see cref="OneCode.Infrastructure.Middleware.EditTransactionMiddleware"/> records every file-edit
/// intent (path + pre-write content) into the ledger before the write, so a crash mid-transaction
/// can be rolled back from the persisted receipt (S-04).
/// </summary>
public sealed record TransactionPersistenceContext(
    IOperationLedger? Ledger,
    string? OperationId,
    long FencingToken = 0)
{
    public bool IsEnabled => Ledger is not null && !string.IsNullOrWhiteSpace(OperationId);

    public static TransactionPersistenceContext Disabled { get; } = new(null, null);
}
