using System.Security.Cryptography;
using Microsoft.Agents.AI.Workflows;
using OneCode.Core.Coordinator;
using OneCode.Core.Workflows;
using OneCode.App.Services.Agent;

namespace OneCode.App.Services.Coordinator;

/// <summary>
/// Durable, serializable payload suspended on the plan-approval RequestPort.
/// The business approval card (PlanSummary/Tasks/Gates) travels via the event sink;
/// this record only carries the identity + plan that must survive checkpoint serialization.
/// </summary>
internal sealed record TeamPlanApprovalInput(
    string RunId,
    string TeamName,
    string PlanSummary,
    IReadOnlyList<string> TaskTitles,
    IReadOnlyList<string> RequiredGates);

/// <summary>The RequestPort response payload produced by the user.</summary>
internal sealed record TeamPlanApprovalDecision(bool Approved);

/// <summary>Terminal output of the plan-approval workflow.</summary>
internal sealed record TeamPlanApprovalOutput(bool Approved);

/// <summary>Result of one plan-approval host invocation.</summary>
internal sealed record TeamApprovalWorkflowResult(
    DurableWorkflowRunResult Durable,
    WorkflowPendingRequest? PendingRequest,
    bool? ApprovalGranted);

/// <summary>Contains one deterministic plan-approval workflow definition.</summary>
internal sealed record TeamApprovalWorkflowDefinition(
    Workflow Workflow,
    WorkflowRunRegistration Registration,
    TeamPlanApprovalInput Input);

/// <summary>
/// Compiles the TeamRun plan approval gate into a MAF RequestPort workflow:
/// <c>start → approval-port (suspends) → sink (terminal)</c>. Approval state lives in the
/// MAF checkpoint + Workflow Run Registry, so a pending decision survives restarts.
/// </summary>
internal sealed class TeamApprovalWorkflowCompiler
{
    private const string PortId = "team-plan-approval-v1";
    private const string StartId = "team-approval-start-v1";
    private const string SinkId = "team-approval-sink-v1";

    public TeamApprovalWorkflowDefinition Compile(
        string teamName,
        TeamRunId runId,
        TeamConfig config,
        string modelId,
        TeamPlanApprovalInput input)
    {
        var start = new TeamApprovalStartExecutor(StartId);
        var port = RequestPort.Create<TeamPlanApprovalInput, TeamPlanApprovalDecision>(PortId);
        var request = port.BindAsExecutor();
        var sink = new TeamApprovalSinkExecutor(SinkId);

        var workflow = new WorkflowBuilder(start)
            .WithName("team-plan-approval-workflow-v1")
            .WithDescription("Suspends on a MAF RequestPort until the user approves the generated TeamRun plan.")
            .AddEdge(start, request, "team-approval-start-edge-v1", false)
            .AddEdge(request, sink, "team-approval-request-edge-v1", false)
            .WithOutputFrom(sink)
            .Build(validateOrphans: true);

        var definitionHash = ComputeApprovalHash(teamName, config, modelId, input);
        var registration = new WorkflowRunRegistration($"team/{runId}/approval", "team-approval", definitionHash);
        return new TeamApprovalWorkflowDefinition(workflow, registration, input);
    }

    private static string ComputeApprovalHash(
        string teamName,
        TeamConfig config,
        string modelId,
        TeamPlanApprovalInput input)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "team-plan-approval-definition-v1");
            // 不依赖 Run 实例 id（RunId 由 Registration.RunId 承载）：等价 plan 产生一致哈希。
            writer.WriteString("team", teamName);
            writer.WriteNumber("mode", (int)config.Mode);
            writer.WriteString("modelId", modelId);
            writer.WriteNumber("maxTurns", config.MaxTurns);
            writer.WriteStartArray("members");
            foreach (var member in config.Members.OrderBy(member => member.AgentId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("agentId", member.AgentId);
                writer.WriteString("role", member.Role);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("planSummary", input.PlanSummary);
            writer.WriteStartArray("tasks");
            foreach (var title in input.TaskTitles.Order(StringComparer.Ordinal))
                writer.WriteStringValue(title);
            writer.WriteEndArray();
            writer.WriteStartArray("requiredGates");
            foreach (var gate in input.RequiredGates.Order(StringComparer.Ordinal))
                writer.WriteStringValue(gate);
            writer.WriteEndArray();
            writer.WriteString("portId", PortId);
            writer.WriteString("contract", "TeamPlanApprovalDecision:v1");
            writer.WriteString("maf", "1.15.0");
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
}

/// <summary>Passes the approval input straight to the RequestPort.</summary>
internal sealed class TeamApprovalStartExecutor(string id)
    : Executor<TeamPlanApprovalInput, TeamPlanApprovalInput>(id)
{
    public override ValueTask<TeamPlanApprovalInput> HandleAsync(
        TeamPlanApprovalInput message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(message);
}

/// <summary>Maps the user's approval decision to the terminal workflow output.</summary>
internal sealed class TeamApprovalSinkExecutor(string id)
    : Executor<TeamPlanApprovalDecision, TeamPlanApprovalOutput>(id)
{
    public override ValueTask<TeamPlanApprovalOutput> HandleAsync(
        TeamPlanApprovalDecision message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new TeamPlanApprovalOutput(message.Approved));
}