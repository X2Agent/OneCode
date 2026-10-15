using System.Threading.Channels;
using OneCode.App.Query;
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
    ILogger? Logger = null);

/// <summary>
/// Runs MainAgentRunner inside one fenced Build attempt. Product transitions remain delegated to
/// IBuildRunCoordinator; the runtime owns only the process-local EditTransaction lifecycle.
/// 文件处置规则（受控 attempt 的有意例外）：只有用户显式取消才回滚；
/// 校验/记账失败、轮数或预算耗尽、异常一律解除自动回滚保留产物——几十轮的产物
/// 不因机器记账问题被整体丢弃。产物落盘后工作树即事实源，善后（回滚/保留）交用户以 git 处置。
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
        var artifactsPreserved = false;

        // 保留产物：解除 dispose 自动回滚。产物留在工作树，善后（保留/回滚）由用户以 git 处置。
        void PreserveArtifacts()
        {
            if (artifactsPreserved)
                return;
            artifactsPreserved = true;
            transaction.PreserveForManualReconciliation();
        }

        var approvedNames = approvedPolicy.ToolNames;
        var approvedSet = approvedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

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
            // 待办与文件记忆工具只读写 agent 自己的会话状态且是步骤进度/暂存笔记的载体，
            // 不在产品工具目录里，必须显式放行，否则模型无法勾选步骤或读取自身会话记忆。
            IsToolAllowed = toolName =>
                approvedSet.Contains(toolName)
                || ToolNames.ReadOnlyTools.Contains(toolName)
                || HarnessProviderTools.Names.Contains(toolName),
            SeedTodoTasks = claimed.Plan?.Tasks,
            PermissionRules = new Dictionary<string, PermissionRuleGroup>(StringComparer.Ordinal)
            {
                ["build-approved-policy"] = new PermissionRuleGroup(
                    AlwaysAllow: approvedNames
                        .Concat(HarnessProviderTools.Names)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
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
        // 受控 attempt 经 durable workflow 调度执行，流枚举链上设置的 ambient AsyncLocal
        // 上下文不保证延续到本层（AsyncLocal 变更不跨 async iterator 的 yield 边界存续）——
        // agent 执行前显式重建，否则依赖会话定位的工具无法解析当前会话。
        var previousConversationId = ToolActivationContext.CurrentConversationId;
        var previousRunId = OneCodeAgentRunContext.CurrentRunId;
        var previousBuildRunId = OneCodeAgentRunContext.CurrentBuildRunId;
        var previousCapabilities = ToolActivationContext.CurrentCapabilities;
        var previousSubagentWriter = OneCodeAgentRunContext.SubagentEventWriter;
        try
        {
            ToolActivationContext.CurrentConversationId = context.Options.ConversationId?.ToString();
            OneCodeAgentRunContext.CurrentRunId = context.Options.AgentRunId;
            OneCodeAgentRunContext.CurrentBuildRunId = input.BuildRunId.ToString();
            ToolActivationContext.CurrentCapabilities = context.Options.ToolCapabilities;
            // 子代理进度旁路与 CurrentBuildRunId 同一模式重建：EventWriter 即 QueryStreamEngine
            // 的 TUI 事件通道，fork 的 AgentTool 经此上报实时进度；不重建则受控 attempt 内
            // 读到 null，进度静默丢失（TUI 状态栏停在"执行 xx"）。
            OneCodeAgentRunContext.SubagentEventWriter = context.EventWriter;
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
                    TerminalReason: RunTerminalReason.Cancelled,
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
            PreserveArtifacts();
            var failed = await coordinator.CompleteAsync(
                input.BuildRunId,
                new MainAgentRunResult(
                    Text: null,
                    TotalInputTokens: 0,
                    TotalOutputTokens: 0,
                    TurnCount: 0,
                    TerminalReason: RunTerminalReason.AgentException,
                    TransactionRolledBack: false,
                    FinalValidationStatus: BuildValidationStatus.Cancelled,
                    ValidationFailureSummary: ex.Message),
                CancellationToken.None,
                fencingToken).ConfigureAwait(false);
            await context.EventWriter.WriteAsync(
                context.BuildStateEventFactory(failed),
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            ToolActivationContext.CurrentConversationId = previousConversationId;
            OneCodeAgentRunContext.CurrentRunId = previousRunId;
            OneCodeAgentRunContext.CurrentBuildRunId = previousBuildRunId;
            ToolActivationContext.CurrentCapabilities = previousCapabilities;
            OneCodeAgentRunContext.SubagentEventWriter = previousSubagentWriter;
        }

        var terminalReason = result.TerminalReason == RunTerminalReason.Completed
            && result.TurnCount >= options.MaxTurns
                ? RunTerminalReason.TurnLimitReached
                : result.TerminalReason;
        if (terminalReason == RunTerminalReason.Cancelled)
            transaction.Rollback();
        else if (terminalReason != RunTerminalReason.Completed)
            PreserveArtifacts();

        var completedResult = result with
        {
            TerminalReason = terminalReason,
            TransactionCommitted = false,
            TransactionRolledBack = terminalReason == RunTerminalReason.Cancelled,
            ModifiedFiles = result.ModifiedFiles ?? transaction.GetModifiedFiles(),
        };
        var buildRun = await coordinator.CompleteAsync(
            input.BuildRunId,
            completedResult,
            ct,
            fencingToken).ConfigureAwait(false);

        // CompleteAsync 的失败收口（验证/归属对账失败转 Blocked 等）发生在 completedResult 构造之后：
        // 此时事务仍带 dispose 自动回滚，必须显式解除，否则 dispose 会静默删掉已落盘的正式产物
        // （历史事故）。只有 Cancelled 才回滚，并保证落盘标记与文件状态一致。
        if (buildRun.State == BuildRunState.Cancelled)
        {
            if (!buildRun.TransactionRolledBack)
            {
                transaction.Rollback();
                buildRun = buildRun with { TransactionRolledBack = true };
                await buildRunStore.SaveFencedAsync(buildRun, buildRun.Version, fencingToken, ct)
                    .ConfigureAwait(false);
            }
        }
        else if (buildRun.State != BuildRunState.Accepting)
        {
            PreserveArtifacts();
        }

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
                transaction.Commit();
            }
            else if (buildRun.State == BuildRunState.Blocked)
                PreserveArtifacts();
            await context.EventWriter.WriteAsync(
            context.BuildStateEventFactory(buildRun),
            ct).ConfigureAwait(false);
        }

        return completedResult with
        {
            TransactionCommitted = buildRun.State == BuildRunState.Completed,
            TransactionRolledBack = buildRun.State == BuildRunState.Cancelled,
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
