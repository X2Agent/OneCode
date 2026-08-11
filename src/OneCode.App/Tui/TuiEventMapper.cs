using OneCode.App.Query;
using OneCode.Core.Coordinator;

namespace OneCode.App.Tui;

/// <summary>
/// Pure static mapping functions that convert backend events
/// (<see cref="QueryEvent"/>, <see cref="OrchestrationEvent"/>) into TUI-layer
/// <see cref="TuiEvent"/> instances.
/// </summary>
/// <remarks>
/// Extracted from <see cref="OneCode.App.Services.InteractiveModeExecutor"/> to
/// keep that class focused on orchestration. These functions have no instance
/// dependencies and no side effects.
/// </remarks>
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
                => new TuiToolStart(id, n, ti),
            ToolDoneEvent { ToolName: var n, IsError: var e, Result: var r, ToolInput: var ti, ToolId: var tid }
                => new TuiToolDone(n, e, r, ti, tid),
            PermissionCheckEvent { ToolName: var tn, Allowed: var a, DenialReason: var dr }
                => new TuiPermissionCheck(tn, a, dr),
            BuildRunStateEvent
            {
                RunId: var runId,
                State: var state,
                SequenceNumber: var sequence,
                ClarificationQuestions: var questions,
                CompletedTasks: var completedTasks,
                TotalTasks: var totalTasks,
                TerminalReason: var terminalReason,
                FailureSummary: var failureSummary,
                Scope: var scope,
                ValidationStatus: var validationStatus,
                ChangedFiles: var changedFiles,
                TurnsCompleted: var turnsCompleted,
                EstimatedCost: var estimatedCost,
                ActiveTasks: var activeTasks,
                BlockedTasks: var blockedTasks,
            } => new TuiBuildRunState(
                runId,
                state,
                sequence,
                questions,
                completedTasks,
                totalTasks,
                terminalReason,
                failureSummary,
                scope,
                validationStatus,
                changedFiles,
                turnsCompleted,
                estimatedCost,
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

    /// <summary>Maps TEAM/GOAL OrchestrationEvent → TuiEvent (pure mapping, except ApprovalRequest which bridges ResponseSource).</summary>
    public static TuiEvent? MapOrchestrationEventToTuiEvent(OrchestrationEvent teamEvt)
    {
        return teamEvt switch
        {
            OrchestrationEvent.AgentCoordination ac
                => new TuiAgentCoordination(ac.FromName, ac.FromColor, ac.ToName, ac.ToColor, ac.Content),
            OrchestrationEvent.AgentMessage am
                => new TuiAgentMessage(am.AgentName, am.AgentColor, am.Content),
            OrchestrationEvent.ToolStart ts
                => new TuiToolStart(ts.ToolId, ts.Name, ts.ToolInput),
            OrchestrationEvent.ToolDone td
                => new TuiToolDone(td.Name, td.IsError, td.Result, td.ToolInput, td.ToolId),
            OrchestrationEvent.TextDelta td
                => new TuiTextDelta(td.Text),
            OrchestrationEvent.FileChanged fc
                => new TuiFileChange(fc.FileName, fc.AddedLines, fc.RemovedLines),
            OrchestrationEvent.Error err
                => new TuiError(err.Message),
            OrchestrationEvent.ApprovalRequest ar
                => MapOrchestrationApprovalRequest(
                    ar.Request.RequestId ?? ar.Request.AgentName ?? "",
                    ar.Request.ToolName,
                    ar.Request.ToolInput,
                    ar.ResponseSource),
            OrchestrationEvent.TeamClarificationRequest clarification
                => new TuiError(
                    $"Team '{clarification.TeamName}' requires clarification before planning:\n" +
                    string.Join("\n", clarification.Questions.Select((question, index) => $"{index + 1}. {question}"))),
            OrchestrationEvent.TeamPlanApprovalRequest approval
                => MapTeamPlanApproval(approval),
            _ => null,
        };
    }

    /// <summary>
    /// Maps TeamPlanApprovalRequest → TuiTeamPlanApproval as a notification-only event.
    /// Plan approval decisions are now handled by the MAF RequestPort workflow
    /// (TeamApprovalWorkflowHost), not by a TUI-side TaskCompletionSource bridge.
    /// </summary>
    private static TuiEvent MapTeamPlanApproval(OrchestrationEvent.TeamPlanApprovalRequest approval)
        => new TuiTeamPlanApproval(
            approval.TeamName,
            approval.PlanSummary,
            approval.Tasks,
            approval.RequiredGates);

    /// <summary>
    /// 映射 OrchestrationEvent.ApprovalRequest → TuiApprovalRequest。
    /// 桥接 ResponseSource，使 TUI 的决策回传到 Team 路径的 inline handler。
    /// </summary>
    private static TuiEvent MapOrchestrationApprovalRequest(
        string agentName,
        string toolName,
        string? toolInput,
        TaskCompletionSource<OneCode.Core.Permissions.ApprovalDecision> responseSource)
    {
        var tuiEvent = new TuiApprovalRequest(agentName, toolName, toolInput);
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

}
