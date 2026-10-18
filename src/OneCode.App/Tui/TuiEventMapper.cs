using OneCode.App.Query;
using OneCode.Core.Coordinator;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Tui;

/// <summary>
/// Pure static mapping functions that convert backend events
/// (<see cref="QueryEvent"/>, <see cref="OrchestrationEvent"/>) into TUI-layer
/// <see cref="TuiEvent"/> instances.
/// </summary>
/// <remarks>No instance dependencies and no side effects.</remarks>
public static class TuiEventMapper
{
    /// <summary>Maps QueryEvent → TuiEvent (pure mapping, no side effects).</summary>
    public static TuiEvent? MapQueryEventToTuiEvent(QueryEvent evt)
    {
        return evt switch
        {
            TextDeltaEvent { Text: var t }
                => new TuiTextDelta(t),
            ThinkingDeltaEvent { Text: var t }
                => new TuiThinkingDelta(t),
            SuggestionsEvent { Items: var items }
                => new TuiSuggestions(items),
            ToolStartEvent { ToolId: var id, ToolName: var n, ToolInput: var ti }
                when !HarnessProviderTools.Names.Contains(n)
                => new TuiToolStart(id, n, ti),
            ToolDoneEvent { ToolName: var n, IsError: var e, Result: var r, ToolInput: var ti, ToolId: var tid }
                when !HarnessProviderTools.Names.Contains(n)
                => new TuiToolDone(n, e, r, ti, tid),
            PermissionCheckEvent { ToolName: var tn, Allowed: var a, DenialReason: var dr }
                => new TuiPermissionCheck(tn, a, dr),
            BuildRunStateEvent
            {
                RunId: var runId,
                State: var state,
                SequenceNumber: var sequence,
                CompletedTasks: var completedTasks,
                TotalTasks: var totalTasks,
                TerminalReason: var terminalReason,
                FailureSummary: var failureSummary,
                Scope: var scope,
                ValidationStatus: var validationStatus,
                ChangedFiles: var changedFiles,
                TurnsCompleted: var turnsCompleted,
                ActiveTasks: var activeTasks,
                BlockedTasks: var blockedTasks,
            } => new TuiBuildRunState(
                runId,
                state,
                sequence,
                completedTasks,
                totalTasks,
                terminalReason,
                failureSummary,
                scope,
                validationStatus,
                changedFiles,
                turnsCompleted,
                activeTasks,
                blockedTasks),
            BuildRunCompletedEvent { Result: var result }
                => new TuiBuildDelivery(result),
            DoneEvent { Usage: var usage, TerminalReason: var reason, TurnsCompleted: var tc, SessionId: var sid, TransactionRolledBack: var rolledBack, ValidationFailureSummary: var validationSummary }
                => new TuiDone(
                    InputTokens: usage?.InputTokens ?? 0,
                    OutputTokens: usage?.OutputTokens ?? 0,
                    TerminalReason: reason,
                    TurnsCompleted: tc,
                    CacheReadTokens: usage?.CacheReadTokens ?? 0,
                    CacheWriteTokens: usage?.CacheWriteTokens ?? 0,
                    SessionId: sid,
                    TransactionRolledBack: rolledBack,
                    ValidationFailureSummary: validationSummary),
            TurnStartedEvent { TurnNumber: var tn }
                => new TuiTurnStarted(tn),
            TurnCompletedEvent { TurnNumber: var tn, HadToolCalls: var htc }
                => new TuiTurnCompleted(tn, htc),
            ToolPoolReadyEvent { TotalTools: var tt, ReadOnlyTools: var rot, McpTools: var mt }
                => new TuiToolPoolReady(tt, rot, mt),
            ErrorEvent { Message: var errMsg }
                => new TuiError(errMsg),
            NoticeEvent { Message: var noticeMsg }
                => new TuiNotice(noticeMsg),
            BudgetWarningEvent { Level: var level, UsedTokens: var used, LimitTokens: var limit }
                => new TuiBudgetWarning(level, used, limit),
            SubagentProgressEvent { ForkId: var fid, AgentName: var an, Phase: var ph, ToolCallCount: var tc, TurnCount: var tn, CurrentToolName: var ct, ElapsedMs: var em }
                => new TuiSubagentProgress(fid, an, ph, tc, tn, ct, em),
            // 审批请求事件映射（传递 ResponseSource 以支持事件驱动回调）
            ApprovalRequestEvent { RequestId: var rid, ToolName: var tn, ToolInput: var ti, ResponseSource: var rs }
                => MapApprovalRequest(rid, tn, ti, rs),
            _ => null,
        };
    }

    /// <summary>
    /// 映射 ApprovalRequestEvent → TuiApprovalRequest。
    /// 需要桥接 ResponseSource（TaskCompletionSource），不能是纯映射。
    /// </summary>
    private static TuiEvent MapApprovalRequest(
        string requestId,
        string toolName,
        string? toolInput,
        TaskCompletionSource<OneCode.Core.Permissions.ApprovalDecision> responseSource)
    {
        var tuiEvent = new TuiApprovalRequest(requestId, toolName, toolInput);
        // 桥接：当 TUI 通过 TuiApprovalRequest.ResponseSource 设置决策时，
        // 转发到原始的 ApprovalRequestEvent.ResponseSource
        tuiEvent.ResponseSource.Task.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
                responseSource.TrySetResult(t.Result);
            else if (t.IsFaulted)
                responseSource.TrySetException(t.Exception is { } ex ? ex : new InvalidOperationException("Approval failed"));
            else if (t.IsCanceled)
                responseSource.TrySetCanceled();
        }, TaskScheduler.Default);
        return tuiEvent;
    }

    /// <summary>Maps GOAL OrchestrationEvent → TuiEvent (pure mapping, except ApprovalRequest which bridges ResponseSource).</summary>
    public static TuiEvent? MapOrchestrationEventToTuiEvent(OrchestrationEvent orchestrationEvt)
    {
        return orchestrationEvt switch
        {
            OrchestrationEvent.AgentMessage am
                => new TuiAgentMessage(am.AgentName, am.AgentColor, am.Content),
            OrchestrationEvent.ToolStart ts
                when !HarnessProviderTools.Names.Contains(ts.Name)
                => new TuiToolStart(ts.ToolId, ts.Name, ts.ToolInput, ts.AgentName),
            OrchestrationEvent.ToolDone td
                when !HarnessProviderTools.Names.Contains(td.Name)
                => new TuiToolDone(td.Name, td.IsError, td.Result, td.ToolInput, td.ToolId, td.AgentName),
            OrchestrationEvent.TextDelta td
                => new TuiTextDelta(td.Text),
            OrchestrationEvent.FileChanged fc
                => new TuiFileChange(fc.FileName, fc.AddedLines, fc.RemovedLines, fc.AgentName),
            OrchestrationEvent.Error err
                => new TuiError(err.Message),
            _ => null,
        };
    }

}
