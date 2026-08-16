using OneCode.Core.Coordinator;

namespace OneCode.App.Services.Coordinator;

public sealed class TeamRunStateMachine
{
    public TeamRun Transition(
        TeamRun current,
        TeamRunPhase nextPhase,
        TeamRunStatus nextStatus,
        DateTimeOffset now)
    {
        if (IsTerminal(current.Status))
            throw new InvalidOperationException($"Terminal TeamRun '{current.Id}' cannot transition from {current.Status}.");
        if (!IsAllowed(current.Phase, current.Status, nextPhase, nextStatus))
        {
            throw new InvalidOperationException(
                $"Illegal TeamRun transition {current.Phase}/{current.Status} -> {nextPhase}/{nextStatus}.");
        }

        var next = current with
        {
            Phase = nextPhase,
            Status = nextStatus,
            Version = checked(current.Version + 1),
            UpdatedAt = now,
        };
        ValidateInvariants(next);
        return next;
    }

    public bool CanCommit(TeamRun run)
        => run.PlanApproved
           && run.TaskGraph is not null
           && run.TaskGraph.RequiredTasks.All(t => t.Status == TeamTaskStatus.Succeeded)
           && run.GateResults
               .Where(g => g.Required)
               .All(g => g.Status == QualityGateStatus.Passed)
           && run.Status == TeamRunStatus.Running
           && run.Phase == TeamRunPhase.Delivery
           && run.Failure is null;

    private static void ValidateInvariants(TeamRun run)
    {
        if (run.Phase == TeamRunPhase.Execution && run.Status == TeamRunStatus.Running && !run.PlanApproved)
            throw new InvalidOperationException("TeamRun cannot execute before plan approval.");
        if (run.Phase == TeamRunPhase.Delivery
            && run.TaskGraph?.RequiredTasks.Any(t => t.Status != TeamTaskStatus.Succeeded) != false)
        {
            throw new InvalidOperationException("TeamRun cannot enter Delivery with incomplete required tasks.");
        }
        if (run.Status == TeamRunStatus.Succeeded
            && (run.Delivery is null || !run.TransactionCommitted))
        {
            throw new InvalidOperationException("Succeeded TeamRun requires DeliveryReport and committed transaction evidence.");
        }
    }

    public static bool IsTerminal(TeamRunStatus status)
        => status is TeamRunStatus.Succeeded
            or TeamRunStatus.Failed
            or TeamRunStatus.Cancelled
            or TeamRunStatus.RolledBack;

    private static bool IsAllowed(
        TeamRunPhase phase,
        TeamRunStatus status,
        TeamRunPhase nextPhase,
        TeamRunStatus nextStatus)
        => (phase, status, nextPhase, nextStatus) switch
        {
            (TeamRunPhase.Intake, TeamRunStatus.Created, TeamRunPhase.Intake, TeamRunStatus.Running) => true,
            (TeamRunPhase.Intake, TeamRunStatus.Running, TeamRunPhase.Clarification, TeamRunStatus.WaitingForUser) => true,
            (TeamRunPhase.Intake, TeamRunStatus.Running, TeamRunPhase.Planning, TeamRunStatus.Running) => true,
            (TeamRunPhase.Clarification, TeamRunStatus.WaitingForUser, TeamRunPhase.Planning, TeamRunStatus.Running) => true,
            (TeamRunPhase.Planning, TeamRunStatus.Running, TeamRunPhase.AwaitingApproval, TeamRunStatus.WaitingForUser) => true,
            (TeamRunPhase.AwaitingApproval, TeamRunStatus.WaitingForUser, TeamRunPhase.Execution, TeamRunStatus.Running) => true,
            (TeamRunPhase.Execution, TeamRunStatus.Running, TeamRunPhase.Verification, TeamRunStatus.Running) => true,
            (TeamRunPhase.Verification, TeamRunStatus.Running, TeamRunPhase.Delivery, TeamRunStatus.Running) => true,
            (TeamRunPhase.Delivery, TeamRunStatus.Running, TeamRunPhase.Completed, TeamRunStatus.Succeeded) => true,
            (_, _, TeamRunPhase.Completed, TeamRunStatus.Cancelled or TeamRunStatus.RolledBack or TeamRunStatus.Failed) => true,
            _ => false,
        };
}
