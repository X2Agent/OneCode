using System.Text;
using OneCode.Core.Coordinator;
using OneCode.Core.Goals;

namespace OneCode.App.Commands;

/// <summary>
/// /resume — resume an interrupted Goal/Team workflow run by sessionId,
/// or list all resumable runs when called without arguments.
///
/// 数据存储：Goal 使用 GoalRun 聚合 + MAF Checkpoint（DurableWorkflowHost）；
/// Team 使用 TeamRun 聚合 + 共享 Durable Workflow Host（跨进程持久）。
/// 适用场景：Goal/Team 执行被 Ctrl+C 中断后，凭 sessionId 恢复。
/// sessionId 类型（Goal/Team）自动判断，用户无需关心。
///
/// 路由说明：
/// 返回 <see cref="CommandResult.ResumeWorkflowResult"/>，TUI dispatch 层
/// 据此直接调用对应的工作流 resume 流，不经过 LLM 查询管线。
/// 不依赖活跃会话（与会话内快照命令 /checkpoint 不同）。
/// </summary>
public sealed class ResumeCommand(IGoalRunStore goalRunStore, ITeamRunStore teamRunStore) : Command
{
    public override string Name => "resume";
    public override string Description => "Resume an interrupted Goal/Team workflow";
    public override CommandCategory Category => CommandCategory.Session;
    public override bool Immediate => true;
    public override string? ArgumentHint => "[sessionId]";

    public override Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
        => args.Length == 0 || string.IsNullOrWhiteSpace(args[0])
            ? ListAllResumableWorkflowSessionsAsync(ct)
            : HandleResumeAsync(args[0], ct);

    /// <summary>可恢复会话集合（Goal 运行 + 去重后的 Team 会话 Id）。</summary>
    private sealed record ResumableSessions(GoalRun[] Goals, SessionId[] Teams);

    private async Task<ResumableSessions> CollectResumableSessionsAsync(CancellationToken ct)
    {
        var goals = (await goalRunStore.ListActiveAsync(ct).ConfigureAwait(false)).ToArray();
        var teams = (await teamRunStore.ListActiveAsync(ct).ConfigureAwait(false))
            .Where(run => run.SessionId is not null)
            .Select(run => run.SessionId!.Value)
            .Distinct()
            .ToArray();
        return new ResumableSessions(goals, teams);
    }

    private async Task<CommandResult> HandleResumeAsync(string sessionId, CancellationToken ct)
    {
        SessionId id = sessionId;

        // Goal 优先（与列表展示顺序一致）。
        var goalRun = await goalRunStore.LoadBySessionAsync(id, ct).ConfigureAwait(false);
        if (goalRun is not null && !goalRun.IsTerminal)
            return CommandResult.ResumeWorkflow(id, WorkflowResumeKind.Goal);

        var teamMatched = (await teamRunStore.ListActiveAsync(ct).ConfigureAwait(false))
            .Any(run => run.SessionId == id
                && run.Status is TeamRunStatus.Running
                    or TeamRunStatus.Blocked
                    or TeamRunStatus.WaitingForUser);
        if (teamMatched)
            return CommandResult.ResumeWorkflow(id, WorkflowResumeKind.Team);

        return CommandResult.Error(await BuildNotFoundMessageAsync(id, ct).ConfigureAwait(false));
    }

    private async Task<CommandResult> ListAllResumableWorkflowSessionsAsync(CancellationToken ct)
    {
        var (goalRuns, teamSessions) = await CollectResumableSessionsAsync(ct).ConfigureAwait(false);

        if (goalRuns.Length == 0 && teamSessions.Length == 0)
        {
            return CommandResult.Text(
                "No interrupted tasks to resume.\n\n" +
                "Goal runs use the durable Workflow Registry and survive process restarts.\n" +
                "Team runs use the durable TeamRun aggregate / Workflow Registry and survive restarts.\n" +
                "Use /resume <sessionId> to continue an interrupted task.");
        }

        var sb = new StringBuilder("Resumable tasks:\n");

        if (goalRuns.Length > 0)
        {
            sb.AppendLine("\n  Goal tasks (durable Workflow Registry):");
            foreach (var run in goalRuns)
            {
                var completed = run.Plan.Count(step => step.State == GoalStepState.Completed);
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"    • {run.SessionId}  ({completed}/{run.Plan.Count} completed, {FormatSavedAt(run.UpdatedAt)}) — {run.Goal}");
            }
        }

        if (teamSessions.Length > 0)
        {
            sb.AppendLine("\n  Team tasks (durable Workflow Registry):");
            foreach (var sid in teamSessions)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"    • {sid}");
            }
        }

        sb.AppendLine("\nUse /resume <sessionId> to continue.");
        return CommandResult.Text(sb.ToString().TrimEnd());
    }

    private async Task<string> BuildNotFoundMessageAsync(SessionId sessionId, CancellationToken ct)
    {
        var (goalRuns, teamSessions) = await CollectResumableSessionsAsync(ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Session '{sessionId}' not found.");
        sb.AppendLine("Goal runs use durable registry/checkpoints; team runs use the durable TeamRun aggregate.");

        if (goalRuns.Length > 0 || teamSessions.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Available sessions:");
            foreach (var run in goalRuns)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  • {run.SessionId}");
            foreach (var sid in teamSessions)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  • {sid}");
        }
        else
        {
            sb.AppendLine("No resumable sessions available.");
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatSavedAt(DateTimeOffset savedAt)
    {
        var delta = DateTimeOffset.UtcNow - savedAt;
        if (delta.TotalMinutes < 1) return "just now";
        if (delta.TotalHours < 1) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalDays < 1) return $"{(int)delta.TotalHours}h ago";
        return $"{(int)delta.TotalDays}d ago";
    }
}
