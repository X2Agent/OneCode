using OneCode.Core.Coordinator;
using OneCode.Core.Errors;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// TeamRun 结果聚合 — 从 <see cref="TeamOrchestrationService"/> 收敛出的纯函数职责：
/// 将 MAF Team DAG 各任务的结构化结果聚合为业务 <see cref="TeamRunResult"/>。
/// </summary>
internal static class TeamResultAggregator
{
    /// <summary>
    /// 任一 Required 任务失败/阻塞/取消，或上游失败导致下游 Blocked，均视为整体失败。
    /// </summary>
    public static TeamRunResult Build(
        string teamName,
        IReadOnlyList<TeamTaskOutcome> outcomes)
    {
        var failed = outcomes
            .Where(outcome => outcome.Status is
                TeamTaskOutcomeStatus.Failed or
                TeamTaskOutcomeStatus.Blocked or
                TeamTaskOutcomeStatus.Cancelled)
            .ToList();
        var succeeded = outcomes
            .Where(outcome => outcome.Status == TeamTaskOutcomeStatus.Succeeded)
            .ToList();

        var hadFailures = failed.Count > 0;
        var errorDetail = string.Join(
            "; ",
            failed.Where(outcome => !string.IsNullOrWhiteSpace(outcome.Error))
                .Select(outcome => $"{outcome.TaskId}: {outcome.Error}"));
        var error = hadFailures && !string.IsNullOrWhiteSpace(errorDetail)
            ? AgentProblemDetails.ToolExecutionFailed(errorDetail, toolName: "TeamOrchestration")
            : null;

        var summary = succeeded.Count > 0
            ? string.Join(
                "\n",
                succeeded.Where(outcome => !string.IsNullOrWhiteSpace(outcome.Summary))
                    .Select(outcome => outcome.Summary))
            : hadFailures
                ? (error?.Detail ?? "Team task execution failed.")
                : "No Team task produced output.";

        return new TeamRunResult(
            teamName,
            summary,
            succeeded.Sum(outcome => outcome.TurnsCompleted),
            succeeded.Any(outcome => outcome.MaxTurnsReached),
            Error: error,
            HadFailures: hadFailures);
    }
}
