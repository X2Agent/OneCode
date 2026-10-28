using OneCode.App.Services.PlanMode;
using OneCode.Core.Build;
using OneCode.Core.Goals;
using OneCode.Core.Session;
using OneCode.Core.Tokens;

namespace OneCode.App.Session;

/// <summary>级联删除的结果：哪些步骤失败、哪些外部目录仍留在磁盘上需要人工处置。</summary>
/// <param name="FailedSteps">失败的步骤名（该步骤未生效，其余步骤仍已执行）。</param>
/// <param name="LeftoverPaths">不随会话删除的目录（当前只有 GOAL 隔离 worktree：可能含未合并改动）。</param>
public sealed record SessionDataRemovalResult(
    IReadOnlyList<string> FailedSteps,
    IReadOnlyList<string> LeftoverPaths)
{
    public static SessionDataRemovalResult Empty { get; } = new([], []);
}

/// <summary>
/// /session delete 的级联清理门面：会话删除此前只删事件日志，摘要索引、用量桶与
/// 三模式的 run 聚合（plans / build-runs / goal-runs）全部成为孤儿。此处按会话
/// 单点收口全部落盘数据 + 内存态用量桶，命令层不再自行拼装删除面。
/// </summary>
public interface ISessionDataCleaner
{
    /// <summary>删除该会话的全部持久化数据与内存态用量桶（幂等）。</summary>
    Task<SessionDataRemovalResult> RemoveAsync(SessionId sessionId, CancellationToken ct = default);
}

/// <summary>
/// 级联删除实现：事件日志 → 摘要索引 → 用量桶（TokenLedger 会话桶）→
/// 计划聚合 → BuildRun → GoalRun。每一步独立容错：单步失败只记日志并计入
/// <see cref="SessionDataRemovalResult.FailedSteps"/>，其余步骤照常执行——
/// 半删除（事件日志已删、run 聚合残留）必须是可见的报告，而不是抛错中断。
/// GOAL 隔离 worktree 目录不自动删除（可能含未合并改动），只作为残留路径回报。
/// </summary>
public sealed class SessionDataCleaner(
    ISessionEventStore eventStore,
    ISessionSummaryIndex summaryIndex,
    ITokenLedger tokenLedger,
    IPlanAggregateStore planStore,
    IBuildRunStore buildRunStore,
    IGoalRunStore goalRunStore,
    ILogger<SessionDataCleaner> logger) : ISessionDataCleaner
{
    public async Task<SessionDataRemovalResult> RemoveAsync(SessionId sessionId, CancellationToken ct = default)
    {
        List<string> failedSteps = [];
        // 残留 worktree 必须在删除 GoalRun 之前读取——run 文件随后就不在了。
        var leftoverPaths = await CollectWorkspaceLeftoversAsync(sessionId, failedSteps, ct).ConfigureAwait(false);

        await RunStepAsync("event log", () => eventStore.DeleteAsync(sessionId, ct), failedSteps).ConfigureAwait(false);
        await RunStepAsync("summary index", () => summaryIndex.RemoveAsync(sessionId, ct), failedSteps).ConfigureAwait(false);
        await RunStepAsync(
            "token bucket",
            () =>
            {
                tokenLedger.RemoveSession(sessionId);
                return Task.CompletedTask;
            },
            failedSteps).ConfigureAwait(false);
        await RunStepAsync("plans", () => planStore.RemoveSessionAsync(sessionId, ct), failedSteps).ConfigureAwait(false);
        await RunStepAsync("build runs", () => buildRunStore.RemoveSessionAsync(sessionId, ct), failedSteps).ConfigureAwait(false);
        await RunStepAsync("goal runs", () => goalRunStore.RemoveSessionAsync(sessionId, ct), failedSteps).ConfigureAwait(false);

        if (failedSteps.Count > 0)
        {
            logger.LogWarning(
                "Session {SessionId} cleanup finished with {Failed} failed step(s): {Steps}",
                sessionId, failedSteps.Count, string.Join(", ", failedSteps));
        }

        return new SessionDataRemovalResult(failedSteps, leftoverPaths);
    }

    private async Task RunStepAsync(string step, Func<Task> action, List<string> failedSteps)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Session data cleanup step '{Step}' failed", step);
            failedSteps.Add(step);
        }
    }

    /// <summary>读取该会话 GoalRun 的隔离 worktree 路径；目录仍存在时作为残留回报。</summary>
    private async Task<IReadOnlyList<string>> CollectWorkspaceLeftoversAsync(
        SessionId sessionId,
        List<string> failedSteps,
        CancellationToken ct)
    {
        GoalRun? run;
        try
        {
            run = await goalRunStore.LoadBySessionAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Session {SessionId} goal run lookup failed; worktree leftovers are unknown", sessionId);
            failedSteps.Add("goal worktree inspection");
            return [];
        }

        var isolatedPath = run?.Workspace?.IsolatedPath;
        if (string.IsNullOrWhiteSpace(isolatedPath) || !Directory.Exists(isolatedPath))
            return [];

        logger.LogInformation(
            "Session {SessionId} leaves an isolated goal worktree on disk: {Path}", sessionId, isolatedPath);
        return [isolatedPath];
    }
}
