using Microsoft.Agents.AI.Workflows;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// Builds MAF ExternalResponse payloads for Team clarification RequestPort resumes.
/// </summary>
internal static class TeamClarificationResponses
{
    public static ExternalResponse Build(string portId, string requestId, string answerText) =>
        new(
            new Microsoft.Agents.AI.Workflows.Checkpointing.RequestPortInfo(
                new Microsoft.Agents.AI.Workflows.Checkpointing.TypeId(typeof(TeamClarificationInput)),
                new Microsoft.Agents.AI.Workflows.Checkpointing.TypeId(typeof(TeamClarificationResponse)),
                portId),
            requestId,
            new Microsoft.Agents.AI.Workflows.PortableValue(
                new TeamClarificationResponse(answerText)));
}
