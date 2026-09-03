using OneCode.Core.Workflows;

namespace OneCode.Core.Goals;

/// <summary>
/// CAS / fencing / ListActive 方法签名由
/// <see cref="IWorkflowRunStore{TRun,TId}"/> 与 <see cref="IActiveWorkflowRunStore{TRun}"/> 内核收编。
/// </summary>
public interface IGoalRunStore : IWorkflowRunStore<GoalRun, GoalRunId>, IActiveWorkflowRunStore<GoalRun>
{
    Task<GoalRun?> LoadBySessionAsync(
        Domain.SessionId sessionId,
        CancellationToken ct = default);

    Task<GoalRun?> LoadByIdAsync(
        GoalRunId runId,
        CancellationToken ct = default);
}
