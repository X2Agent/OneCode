using System.Security.Cryptography;
using System.Text;
using OneCode.Core.PlanMode;

namespace OneCode.App.Services.PlanMode;

/// <summary>
/// <see cref="PlanWorkflowApplicationService"/> 的持久化与实体构造辅助
/// （起拆分为 partial：本文件承载统一持久化出口、聚合加载
/// 与 revision/snapshot 构造，与状态机转换面分离）。
/// </summary>
public sealed partial class PlanWorkflowApplicationService
{
    private async Task<PlanWorkflow> RequireWorkflowAsync(
        SessionId sessionId,
        PlanWorkflowId planId,
        CancellationToken ct)
        => (await RequireAggregateAsync(sessionId, planId, ct).ConfigureAwait(false)).Workflow;

    private async Task<PlanAggregate> RequireAggregateAsync(
        SessionId sessionId,
        PlanWorkflowId planId,
        CancellationToken ct)
    {
        var aggregate = await aggregateStore.LoadAsync(sessionId, ct).ConfigureAwait(false)
            ?? throw new PlanTransitionException($"No active plan workflow exists for session '{sessionId}'.");
        if (aggregate.Workflow.Id != planId)
            throw new PlanTransitionException($"Plan '{planId}' does not belong to session '{sessionId}'.");
        return aggregate;
    }

    /// <summary>
    /// 统一持久化出口：聚合已 claim 时必须携带与磁盘一致的
    /// <paramref name="commandFencingToken"/> 走 fenced 写（错配 fail-closed）；
    /// 未 claim 时走普通保存，但拒绝命令携带令牌（与其余三模式内核语义一致）。
    /// </summary>
    private async Task SaveWorkflowAsync(
        PlanWorkflow workflow,
        long expectedVersion,
        long commandFencingToken,
        CancellationToken ct)
    {
        var aggregate = await RequireAggregateAsync(workflow.SessionId, workflow.Id, ct).ConfigureAwait(false);
        if (aggregate.Workflow.WorkflowFencingToken is { } claimedToken)
        {
            if (commandFencingToken != claimedToken)
            {
                throw new PlanConcurrencyException(
                    $"Plan workflow '{workflow.Id}' fenced write rejected: command fencing token {commandFencingToken} does not match claimed token {claimedToken}.");
            }

            await aggregateStore.SaveFencedAsync(
                aggregate with { Workflow = workflow },
                expectedVersion,
                claimedToken,
                ct).ConfigureAwait(false);
            return;
        }

        if (commandFencingToken != 0)
        {
            throw new PlanConcurrencyException(
                $"Plan workflow '{workflow.Id}' must be claimed before fenced writes.");
        }

        await aggregateStore.SaveAsync(
            aggregate with { Workflow = workflow },
            expectedVersion,
            ct).ConfigureAwait(false);
    }

    private static PlanRevision CreateRevision(
        PlanWorkflow workflow,
        string title,
        string markdown,
        IReadOnlyList<PlanStepDefinition> steps,
        IReadOnlyList<string> risks,
        IReadOnlyList<string> assumptions,
        PlanRevisionStatus status)
        => new()
        {
            PlanId = workflow.Id,
            SessionId = workflow.SessionId,
            Revision = workflow.LatestRevision + 1,
            Title = title,
            Markdown = markdown,
            Steps = steps,
            Risks = risks,
            Assumptions = assumptions,
            ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(markdown))).ToLowerInvariant(),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static PlanWorkflow Failure(
        PlanWorkflow current,
        string code,
        string message,
        DateTimeOffset occurredAt)
        => current with
        {
            State = PlanWorkflowState.Failed,
            LastErrorCode = code,
            LastErrorMessage = message,
            Version = current.Version + 1,
            UpdatedAt = occurredAt,
        };

    private static PlanRevisionResult DuplicateRevisionResult(PlanAggregate aggregate)
    {
        var revisionNumber = aggregate.Workflow.LastProcessedRevision
            ?? throw new PlanTransitionException("Duplicate revision command is missing its persisted revision reference.");
        var revision = aggregate.FindRevision(revisionNumber)
            ?? throw new PlanTransitionException($"Duplicate revision {revisionNumber} is missing from the Plan aggregate.");
        return new PlanRevisionResult(aggregate.Workflow, revision);
    }

    private static ApprovedPlanSnapshot FreezeApprovedSnapshot(PlanRevision revision, string approvedBy)
        => new()
        {
            PlanId = revision.PlanId,
            SessionId = revision.SessionId,
            Revision = revision.Revision,
            Title = revision.Title,
            Markdown = revision.Markdown,
            Steps = revision.Steps,
            ContentHash = revision.ContentHash,
            ApprovedBy = approvedBy,
            ApprovedAt = DateTimeOffset.UtcNow,
        };

    private static Task<T> ExecuteAsync<T>(Func<Task<T>> action) => action();
}
