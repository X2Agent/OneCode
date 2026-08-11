using System.Security.Cryptography;
using Microsoft.Agents.AI.Workflows;
using OneCode.App.Services.Agent;
using OneCode.Core.Coordinator;
using OneCode.Core.Workflows;

namespace OneCode.App.Services.Coordinator;

internal sealed record TeamClarificationInput(
    string RunId,
    string TeamName,
    IReadOnlyList<string> Questions);

internal sealed record TeamClarificationResponse(string Answer);

internal sealed record TeamClarificationOutput(string Answer);

internal sealed record TeamClarificationResult(
    DurableWorkflowRunResult Durable,
    WorkflowPendingRequest? PendingRequest,
    string? Answer);

internal sealed class TeamClarificationWorkflowCompiler
{
    private const string PortId = "team-clarification-v1";
    private const string StartId = "team-clarification-start-v1";
    private const string SinkId = "team-clarification-sink-v1";

    public (Workflow Workflow, WorkflowRunRegistration Registration, TeamClarificationInput Input) Compile(
        string teamName,
        TeamRunId runId,
        TeamConfig config,
        string modelId,
        TeamClarificationInput input)
    {
        var start = new TeamClarificationStartExecutor(StartId);
        var port = RequestPort.Create<TeamClarificationInput, TeamClarificationResponse>(PortId);
        var request = port.BindAsExecutor();
        var sink = new TeamClarificationSinkExecutor(SinkId);
        var workflow = new WorkflowBuilder(start)
            .WithName("team-clarification-workflow-v1")
            .AddEdge(start, request, "team-clarification-start-v1", false)
            .AddEdge(request, sink, "team-clarification-sink-v1", false)
            .WithOutputFrom(sink)
            .Build(validateOrphans: true);

        var canonical = JsonSerializer.Serialize(new
        {
            schema = "team-clarification-definition-v1",
            teamName,
            mode = config.Mode,
            modelId,
            questions = input.Questions.Order(StringComparer.Ordinal),
            portId = PortId,
        });
        var hash = Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return (workflow, new WorkflowRunRegistration(
            $"team/{runId}/clarification",
            "team-clarification",
            hash), input);
    }
}

internal sealed class TeamClarificationWorkflowHost(
    IDurableWorkflowHost durableHost,
    TeamClarificationWorkflowCompiler compiler,
    IWorkflowRunRegistry registry)
{
    public async Task<TeamClarificationResult> RunAsync(
        string teamName,
        TeamRunId runId,
        TeamConfig config,
        string modelId,
        TeamClarificationInput input,
        JsonSerializerOptions serializerOptions,
        ExternalResponse? externalResponse = null,
        CancellationToken ct = default)
    {
        var definition = compiler.Compile(teamName, runId, config, modelId, input);
        var record = await registry.LoadAsync(definition.Registration.RunId, ct).ConfigureAwait(false);
        var durable = await durableHost.RunAsync(
            definition.Registration,
            definition.Workflow,
            definition.Input,
            $"team/{runId}/clarification",
            serializerOptions,
            executionGeneration: record is null ? 1 : null,
            externalResponse: externalResponse,
            ct: ct).ConfigureAwait(false);
        var pending = durable.Events
            .OfType<WorkflowRuntimeEvent.PendingRequest>()
            .Select(item => item.Request)
            .LastOrDefault();
        var answer = durable.Events
            .OfType<WorkflowRuntimeEvent.Output>()
            .Select(item => item.Value)
            .OfType<TeamClarificationOutput>()
            .SingleOrDefault()?.Answer;
        return new TeamClarificationResult(durable, pending, answer);
    }
}

internal sealed class TeamClarificationStartExecutor(string id)
    : Executor<TeamClarificationInput, TeamClarificationInput>(id)
{
    public override ValueTask<TeamClarificationInput> HandleAsync(
        TeamClarificationInput message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(message);
}

internal sealed class TeamClarificationSinkExecutor(string id)
    : Executor<TeamClarificationResponse, TeamClarificationOutput>(id)
{
    public override ValueTask<TeamClarificationOutput> HandleAsync(
        TeamClarificationResponse message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new TeamClarificationOutput(message.Answer));
}
