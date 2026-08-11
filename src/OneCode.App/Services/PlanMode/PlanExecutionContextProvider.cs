using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Query;
using OneCode.App.Services.Context;
using OneCode.Core.PlanMode;

namespace OneCode.App.Services.PlanMode;

/// <summary>
/// Injects the immutable approved-plan snapshot while its Build run is active.
/// The persisted workflow is the only source; mutable legacy plan files are never read.
///
/// Turn count is tracked per-conversation to prevent cross-conversation contamination.
/// </summary>
public sealed class PlanExecutionContextProvider(
    IPlanWorkflowApplicationService workflowService,
    IPermissionModeProvider modeProvider)
    : ReadOnlyAIContextProviderBase
{
    private const int FullPlanRefreshInterval = 5;

    private readonly ConcurrentDictionary<SessionId, PlanExecutionCacheEntry> _cache = new();

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken ct)
    {
        if (modeProvider.CurrentMode != PermissionMode.AcceptEdits)
            return new AIContext();

        if (SessionId.TryParse(ToolActivationContext.CurrentConversationId) is not { } sessionId)
            return new AIContext();

        var workflow = await workflowService.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (workflow?.ApprovedSnapshot is not { } snapshot
            || workflow.State is not (PlanWorkflowState.StartingExecution
                or PlanWorkflowState.Executing
                or PlanWorkflowState.Verifying))
        {
            ResetTurnCount(sessionId);
            return new AIContext();
        }

        var key = $"{snapshot.PlanId}:{snapshot.Revision}:{snapshot.ContentHash}";
        var entry = _cache.AddOrUpdate(
            sessionId,
            _ => new PlanExecutionCacheEntry(key, snapshot, 1),
            (_, current) => string.Equals(current.PlanKey, key, StringComparison.Ordinal)
                ? current with { TurnCount = current.TurnCount + 1 }
                : new PlanExecutionCacheEntry(key, snapshot, 1));
        var currentTurn = entry.TurnCount;
        var approved = entry.Snapshot;
        if (currentTurn == 1 || currentTurn % FullPlanRefreshInterval == 0)
        {
            return new AIContext
            {
                Messages =
                [
                    new ChatMessage(ChatRole.System, BuildFullContext(approved)),
                ],
            };
        }

        return new AIContext
        {
            Messages =
            [
                new ChatMessage(ChatRole.System,
                    $"[Approved Plan {approved.PlanId} r{approved.Revision} · {approved.ContentHash[..Math.Min(12, approved.ContentHash.Length)]} · Turn {currentTurn}] " +
                    "Continue executing the immutable approved snapshot. Persist step progress and verification evidence through the plan execution tools."),
            ],
        };
    }

    public void ResetTurnCount(SessionId conversationId)
        => _cache.TryRemove(conversationId, out _);

    private sealed record PlanExecutionCacheEntry(
        string PlanKey,
        ApprovedPlanSnapshot Snapshot,
        int TurnCount);

    private static string BuildFullContext(ApprovedPlanSnapshot snapshot)
    {
        var steps = string.Join("\n", snapshot.Steps.Select(step =>
            $"- {step.Id}: {step.Title}\n  {step.Description}\n  Acceptance: {string.Join("; ", step.AcceptanceCriteria)}"));
        return $"""
            ## IMMUTABLE APPROVED PLAN
            Plan: {snapshot.PlanId}
            Revision: {snapshot.Revision}
            Content hash: {snapshot.ContentHash}

            {snapshot.Markdown}

            ## STRUCTURED STEPS
            {steps}

            Execute exactly this approved snapshot. Use UpdatePlanStep for progress,
            CompletePlanExecution before verification, and CompletePlanVerification with concrete evidence.
            """;
    }
}
