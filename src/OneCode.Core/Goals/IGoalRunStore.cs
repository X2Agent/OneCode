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

    /// <summary>
    /// 删除该会话的全部 GoalRun 落盘数据（run 文件 + 归档），供 /session delete 级联清理。
    /// 实现为目录级删除；目录不存在时幂等返回。
    /// </summary>
    Task RemoveSessionAsync(
        Domain.SessionId sessionId,
        CancellationToken ct = default);
}
