using OneCode.Core.Coordinator;
using OneCode.Core.Errors;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// Shared TeamRunResult error factory — extracted from <see cref="TeamOrchestrationService"/>.
/// Emits OrchestrationEvent.Error when a sink is provided.
/// </summary>
internal static class TeamRunErrors
{
    public static TeamRunResult Fail(
        string teamName,
        string detail,
        Action<OrchestrationEvent>? eventSink = null)
    {
        var problem = AgentProblemDetails.ToolExecutionFailed(detail, toolName: "TeamOrchestration");
        eventSink?.Invoke(new OrchestrationEvent.Error(problem.Detail, problem));
        return new TeamRunResult(teamName, problem.Detail, 0, false, Error: problem);
    }
}
