using OneCode.Core.Coordinator;
using OneCode.Core.Errors;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// TeamRun 的纯守卫/判定函数集：计划等价比较、执行失败归因、错误指纹、fencing 校验、
/// 计划合法性（含依赖环检测）。全部为静态方法，违规即抛异常——
/// 校验规则的回归由此处的测试锚定。
/// </summary>
internal static class TeamRunGuards
{
    public static bool PlansMatch(ImplementationPlan left, ImplementationPlan right)
        => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    public static AgentProblemDetails? ResolveExecutionFailure(TeamRunResult execution)
    {
        if (execution.Error is not null)
            return execution.Error;
        if (execution.HadFailures)
        {
            return AgentProblemDetails.ToolExecutionFailed(
                "Team workflow reported one or more agent failures.",
                toolName: "TeamTaskExecution");
        }
        if (execution.MaxTurnsReached)
        {
            return AgentProblemDetails.ToolExecutionFailed(
                "Team workflow reached its maximum turn limit before completing the task.",
                toolName: "TeamTaskExecution");
        }
        if (execution.TurnsCompleted == 0
            || string.IsNullOrWhiteSpace(execution.Output)
            || string.Equals(execution.Output.Trim(), "(no output)", StringComparison.Ordinal))
        {
            return AgentProblemDetails.ToolExecutionFailed(
                "Team workflow completed without any agent response.",
                toolName: "TeamTaskExecution",
                suggestedNextAction: "Check the ChatClient/model configuration and Team workflow logs.");
        }
        return null;
    }

    public static string ComputeErrorFingerprint(string error)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(error))).ToLowerInvariant()[..16];

    public static void RequireFence(TeamRun run, long? fencingToken)
    {
        if (fencingToken is not { } token)
            return;
        if (run.WorkflowFencingToken != token)
        {
            throw new InvalidOperationException(
                $"TeamRun '{run.Id}' fencing token mismatch: expected {run.WorkflowFencingToken?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}, attempted {token}.");
        }
    }

    public static void ValidatePlan(ImplementationPlan plan)
    {
        if (plan.Tasks.Count == 0)
            throw new InvalidOperationException("Team implementation plan must contain tasks.");
        if (plan.RequiredGates.All(g => !g.Required))
            throw new InvalidOperationException("Team implementation plan must contain a required quality gate.");
        var ids = plan.Tasks.Select(t => t.Id).ToList();
        if (ids.Count != ids.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidOperationException("Team task IDs must be unique.");
        if (plan.Tasks.SelectMany(t => t.DependsOn).Any(id => !ids.Contains(id, StringComparer.Ordinal)))
            throw new InvalidOperationException("Team task dependency references an unknown task.");
        ValidateAcyclic(plan.Tasks);
        if (plan.Tasks.Where(t => t.ToolPolicy == TeamToolPolicy.WriteAllowed)
            .Any(t => t.AcceptanceCriteria.Count == 0))
        {
            throw new InvalidOperationException("Every Team write task requires acceptance criteria.");
        }
    }

    private static void ValidateAcyclic(IReadOnlyList<TeamTaskDefinition> tasks)
    {
        var dependencies = tasks.ToDictionary(
            task => task.Id,
            task => task.DependsOn,
            StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var task in tasks)
        {
            if (HasCycle(task.Id))
                throw new InvalidOperationException("Team task graph contains a dependency cycle.");
        }

        bool HasCycle(string taskId)
        {
            if (visited.Contains(taskId))
                return false;
            if (!visiting.Add(taskId))
                return true;
            foreach (var dependency in dependencies[taskId])
            {
                if (HasCycle(dependency))
                    return true;
            }
            visiting.Remove(taskId);
            visited.Add(taskId);
            return false;
        }
    }
}
