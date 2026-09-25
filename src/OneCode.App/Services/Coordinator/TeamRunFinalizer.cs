using OneCode.App.Services.Runtime;
using OneCode.Core.Coordinator;
using OneCode.Core.Errors;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// CompleteExecution 的验证 / 交付 / 回滚。
/// </summary>
internal sealed class TeamRunFinalizer(
    ITeamRunStore store,
    TeamRunStateMachine stateMachine,
    WorkflowQualityGateRunner qualityGateRunner,
    DeliveryReportBuilder deliveryReportBuilder)
{
    public async Task<TeamRun> CompleteExecutionAsync(
        TeamRun run,
        TeamRunResult execution,
        EditTransaction transaction,
        IReadOnlyList<FileChange> fileChanges,
        long? fencingToken,
        CancellationToken ct,
        OneCode.Core.Workflows.IOperationLedger? operationLedger = null,
        string? operationId = null)
    {
        TeamRunGuards.RequireFence(run, fencingToken);
        var taskStatus = TeamRunGuards.ResolveExecutionFailure(execution) is not null
            || run.TaskGraph?.RequiredTasks.Any(task => task.Status != TeamTaskStatus.Succeeded) != false
            ? TeamTaskStatus.Failed
            : TeamTaskStatus.Succeeded;
        var updated = run with
        {
            Changes = new ChangeSetSummary(
                fileChanges,
                fileChanges.Sum(f => f.AddedLines.Count),
                fileChanges.Sum(f => f.RemovedLines.Count)),
            Failure = TeamRunGuards.ResolveExecutionFailure(execution),
        };

        if (taskStatus != TeamTaskStatus.Succeeded)
        {
            updated = updated with
            {
                Failure = updated.Failure ?? AgentProblemDetails.ToolExecutionFailed(
                    "One or more required Team tasks did not succeed.",
                    toolName: "TeamTaskExecution"),
            };
            return await RollBackAsync(
                updated,
                transaction,
                run.Version,
                "Team execution failed; file changes were rolled back.",
                ct).ConfigureAwait(false);
        }

        updated = stateMachine.Transition(
            updated,
            TeamRunPhase.Verification,
            TeamRunStatus.Running,
            DateTimeOffset.UtcNow);
        await SaveOrThrowAsync(updated, run.Version, ct).ConfigureAwait(false);

        var gateResults = await qualityGateRunner.RunAsync(
            updated.Plan!.RequiredGates,
            updated.WorkingDirectory,
            transaction,
            updated,
            ct).ConfigureAwait(false);
        var requiredGatesPassed = gateResults.Where(g => g.Required)
            .All(g => g.Status == QualityGateStatus.Passed);
        updated = updated with { GateResults = gateResults };

        if (!requiredGatesPassed)
        {
            return await RollBackAsync(
                updated,
                transaction,
                updated.Version,
                "Required Team quality gates failed; file changes were rolled back.",
                ct).ConfigureAwait(false);
        }

        updated = stateMachine.Transition(
            updated,
            TeamRunPhase.Delivery,
            TeamRunStatus.Running,
            DateTimeOffset.UtcNow);
        await SaveOrThrowAsync(updated, updated.Version - 1, ct).ConfigureAwait(false);

        if (!stateMachine.CanCommit(updated))
        {
            return await RollBackAsync(
                updated,
                transaction,
                updated.Version,
                "TeamRun CanCommit invariant rejected delivery.",
                ct).ConfigureAwait(false);
        }

        var delivery = deliveryReportBuilder.Build(updated, committed: true, execution.Output);
        updated = updated with
        {
            Delivery = delivery,
            TransactionCommitted = true,
        };
        updated = stateMachine.Transition(
            updated,
            TeamRunPhase.Completed,
            TeamRunStatus.Succeeded,
            DateTimeOffset.UtcNow);

        // Persist the deterministic commit decision before releasing transaction snapshots.
        // If persistence fails, the caller's using scope still disposes the uncommitted
        // transaction and restores files. EditTransaction.Commit performs no external I/O.
        await SaveOrThrowAsync(updated, updated.Version - 1, ct).ConfigureAwait(false);

        // S-04: 先持久化提交（ledger receipt）再内存提交——防止"内存已提交、ledger 未提交"崩溃后误回滚。
        if (operationLedger is not null && operationId is not null && fencingToken is { } fence)
        {
            await operationLedger.CommitTransactionAsync(
                operationId,
                fence,
                $"team-execution-committed:{run.Id}",
                ct).ConfigureAwait(false);
        }

        transaction.Commit();
        return updated;
    }

    private async Task<TeamRun> RollBackAsync(
        TeamRun run,
        EditTransaction transaction,
        long expectedVersion,
        string summary,
        CancellationToken ct,
        long? fencingToken = null)
    {
        transaction.Rollback();
        var delivery = run.TaskGraph is null
            ? null
            : deliveryReportBuilder.Build(run, committed: false, summary);
        var rolledBack = run with { Delivery = delivery };
        rolledBack = stateMachine.Transition(
            rolledBack,
            TeamRunPhase.Completed,
            TeamRunStatus.RolledBack,
            DateTimeOffset.UtcNow);
        await SaveOrThrowAsync(rolledBack, expectedVersion, ct).ConfigureAwait(false);
        return rolledBack;
    }

    private async Task SaveOrThrowAsync(TeamRun run, long expectedVersion, CancellationToken ct)
    {
        if (!await store.TrySaveAsync(run, expectedVersion, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"TeamRun '{run.Id}' version conflict while saving version {run.Version}.");
    }

}
