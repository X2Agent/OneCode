using System.Threading.Channels;
using OneCode.App.Services.Agent;
using OneCode.Core.Build;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.BuildMode;

/// <summary>Process-local dependencies for one controlled Build attempt.</summary>
public sealed record ControlledBuildAttemptContext(
    MainAgentRunOptions Options,
    ChannelWriter<object> EventWriter,
    Func<EditTransaction> TransactionFactory,
    Func<BuildRun, object> BuildStateEventFactory,
    OneCode.Core.Workflows.IOperationLedger? Ledger = null);

/// <summary>
/// Runs MainAgentRunner inside one fenced Build attempt. Product transitions remain delegated to
/// IBuildRunCoordinator; the runtime owns only the process-local EditTransaction lifecycle.
/// </summary>
public sealed class ControlledBuildAttemptRuntime(
    IMainAgentRunner mainAgentRunner,
    IBuildRunCoordinator coordinator,
    IBuildRunStore buildRunStore,
    ControlledBuildAttemptContext context) : IControlledBuildAttemptRuntime
{
    public async Task<MainAgentRunResult> ExecuteAsync(
        ControlledBuildAttemptInput input,
        CancellationToken ct = default)
    {
        var claimed = await buildRunStore.LoadByIdAsync(input.BuildRunId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"BuildRun '{input.BuildRunId}' was not found.");
        if (claimed.WorkflowFencingToken is not { } fencingToken)
            throw new InvalidOperationException($"BuildRun '{input.BuildRunId}' has not been workflow-claimed.");
        if (claimed.State != BuildRunState.Implementing)
        {
            throw new InvalidOperationException(
                $"BuildRun '{input.BuildRunId}' cannot execute from state '{claimed.State}'.");
        }
        if (claimed.ApprovedToolPolicy is not { ToolNames.Count: > 0 } approvedPolicy)
        {
            // Fail-closed for legacy runs that reached Implementing without a plan-approval record:
            // an attempt must never execute without an approved tool policy.
            throw new InvalidOperationException(
                $"BuildRun '{input.BuildRunId}' has no approved tool policy; attempt execution is denied.");
        }

        using var transaction = context.TransactionFactory();
        var approvedNames = approvedPolicy.ToolNames;
        var approvedSet = approvedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // S-04: 持久化 Operation Ledger。新世代开始前回滚本 Run 上一世代遗留的未提交文件副作用，
        // 然后开启本 attempt 的持久化事务；每次文件编辑的 intent 由 EditTransactionMiddleware 落盘。
        var ledger = context.Ledger;
        if (ledger is not null)
        {
            await ledger.ReconcileRunAsync($"build/{input.BuildRunId}", ct).ConfigureAwait(false);
            await ledger.BeginTransactionAsync(
                input.OperationId,
                "file-transaction",
                fencingToken,
                ct).ConfigureAwait(false);
            transaction.PersistTo(ledger, input.OperationId, fencingToken);
        }

        var options = context.Options with
        {
            SharedTransaction = transaction,
            DeferTransactionCommit = true,
            // Tool permissions were confirmed up-front at the plan gate. Attempt execution must
            // never suspend on an in-process TaskCompletionSource approval dialog: approve the
            // approved policy, deny everything else (fail-closed), and suppress the interactive
            // broker entirely.
            ToolCapabilities = ControlledBuildAttemptWorkflowCompiler.ApprovedPolicyCapabilities(
                claimed,
                context.Options.ToolCapabilities),
            IsToolAllowed = toolName =>
                approvedSet.Contains(toolName) || ToolNames.ReadOnlyTools.Contains(toolName),
            PermissionRules = new Dictionary<string, PermissionRuleGroup>(StringComparer.Ordinal)
            {
                ["build-approved-policy"] = new PermissionRuleGroup(
                    AlwaysAllow: approvedNames
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .Select(toolName => new PermissionRule(toolName))
                        .ToArray()),
            },
            ApprovalBroker = null,
            SuppressToolApproval = true,
            BeforeFinalValidation = async validationCt =>
            {
                var verifying = await coordinator.BeginVerificationAsync(
                    input.BuildRunId,
                    validationCt,
                    fencingToken).ConfigureAwait(false);
                await context.EventWriter.WriteAsync(
                    context.BuildStateEventFactory(verifying),
                    validationCt).ConfigureAwait(false);
            },
        };

        MainAgentRunResult result;
        try
        {
            result = await mainAgentRunner.RunStreamingAsync(
                options,
                new NonCompletingChannelWriter(context.EventWriter),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            transaction.Rollback();
            var cancelled = await coordinator.CompleteAsync(
                input.BuildRunId,
                new MainAgentRunResult(
                    Text: null,
                    TotalInputTokens: 0,
                    TotalOutputTokens: 0,
                    TurnCount: 0,
                    TerminalReason: BuildTerminalReason.Cancelled,
                    TransactionRolledBack: true,
                    FinalValidationStatus: BuildValidationStatus.Cancelled),
                CancellationToken.None,
                fencingToken).ConfigureAwait(false);
            await context.EventWriter.WriteAsync(
                context.BuildStateEventFactory(cancelled),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            var failed = await coordinator.CompleteAsync(
                input.BuildRunId,
                new MainAgentRunResult(
                    Text: null,
                    TotalInputTokens: 0,
                    TotalOutputTokens: 0,
                    TurnCount: 0,
                    TerminalReason: BuildTerminalReason.AgentException,
                    TransactionRolledBack: true,
                    FinalValidationStatus: BuildValidationStatus.Cancelled,
                    ValidationFailureSummary: ex.Message),
                CancellationToken.None,
                fencingToken).ConfigureAwait(false);
            await context.EventWriter.WriteAsync(
                context.BuildStateEventFactory(failed),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var terminalReason = result.TerminalReason == BuildTerminalReason.Completed
            && result.TurnCount >= options.MaxTurns
                ? BuildTerminalReason.TurnLimitReached
                : result.TerminalReason;
        if (terminalReason != BuildTerminalReason.Completed)
            transaction.Rollback();

        var completedResult = result with
        {
            TerminalReason = terminalReason,
            TransactionCommitted = false,
            TransactionRolledBack = terminalReason != BuildTerminalReason.Completed,
            ModifiedFiles = result.ModifiedFiles ?? transaction.GetModifiedFiles(),
        };
        var buildRun = await coordinator.CompleteAsync(
            input.BuildRunId,
            completedResult,
            ct,
            fencingToken).ConfigureAwait(false);
        await context.EventWriter.WriteAsync(
            context.BuildStateEventFactory(buildRun),
            ct).ConfigureAwait(false);

        if (buildRun.State == BuildRunState.Accepting)
        {
            buildRun = await coordinator.ConfirmCommitAsync(
                input.BuildRunId,
                ct,
                fencingToken).ConfigureAwait(false);
            if (buildRun.State == BuildRunState.Completed)
            {
                // S-04: 先持久化提交（ledger receipt），再内存提交——若在两者之间崩溃，
                // ledger 已标记 committed，恢复时不会回滚这批已确认写入。
                if (ledger is not null)
                {
                    await ledger.CommitTransactionAsync(
                        input.OperationId,
                        fencingToken,
                        $"build-attempt:{input.BuildRunId}:{input.Attempt}:{terminalReason}",
                        ct).ConfigureAwait(false);
                }

                transaction.Commit();
            }
            else if (buildRun.State == BuildRunState.Blocked)
                transaction.PreserveForManualReconciliation();
            await context.EventWriter.WriteAsync(
            context.BuildStateEventFactory(buildRun),
            ct).ConfigureAwait(false);
        }

        return completedResult with
        {
            TransactionCommitted = buildRun.State == BuildRunState.Completed,
            TransactionRolledBack = buildRun.State is not (BuildRunState.Completed or BuildRunState.Blocked),
        };
    }

    private sealed class NonCompletingChannelWriter(ChannelWriter<object> inner) : ChannelWriter<object>
    {
        public override bool TryComplete(Exception? error = null) => true;

        public override bool TryWrite(object item) => inner.TryWrite(item);

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
            => inner.WaitToWriteAsync(cancellationToken);

        public override ValueTask WriteAsync(object item, CancellationToken cancellationToken = default)
            => inner.WriteAsync(item, cancellationToken);
    }
}
