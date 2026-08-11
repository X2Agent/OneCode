namespace OneCode.Core.Permissions;

/// <summary>
/// Provides the single interaction boundary for conditional tool approvals.
/// Implementations must fail closed when no approval channel is available.
/// </summary>
public interface IApprovalBroker
{
    Task<ApprovalDecision> RequestAsync(
        ApprovalRequest request,
        CancellationToken ct = default);
}
