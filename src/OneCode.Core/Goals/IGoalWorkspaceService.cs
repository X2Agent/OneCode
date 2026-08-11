namespace OneCode.Core.Goals;

public interface IGoalWorkspaceService
{
    Task<GoalWorkspaceSnapshot> PrepareAsync(
        GoalRun run,
        CancellationToken ct = default);

    Task<GoalStepReceipt?> FindStepReceiptAsync(
        GoalRun run,
        int goalId,
        long fencingToken,
        CancellationToken ct = default);

    Task<GoalStepReceipt> RecordStepAsync(
        GoalRun run,
        GoalStepExecutionEvidence evidence,
        long fencingToken,
        CancellationToken ct = default);

    Task<GoalPublishReceipt> PublishAsync(
        GoalRun run,
        long fencingToken,
        CancellationToken ct = default);

    Task CleanupAsync(
        GoalRun run,
        long fencingToken,
        CancellationToken ct = default);
}
