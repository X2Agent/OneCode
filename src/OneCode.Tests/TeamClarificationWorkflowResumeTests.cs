using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Services.Agent;
using OneCode.App.Services.Coordinator;
using OneCode.Core.Coordinator;
using OneCode.Infrastructure.Workflows;

namespace OneCode.Tests;

/// <summary>
/// Product-level HITL recovery tests for the Team clarification workflow.
///
/// These tests verify S-03 product requirements (not just MAF RequestPort mechanics):
///   1. A workflow that suspends on the clarification RequestPort can be resumed from a
///      brand-new host instance pointing at the same on-disk Registry + Checkpoint store
///      (simulates a process restart).
///   2. RequestId, PortId and business CommandId stay identical across the restart, so an
///      externally persisted response can be routed back to the original pending request.
///   3. Wrong RequestId / wrong PortId / post-terminal responses are rejected fail-closed.
///
/// Cross-process Lease/FencingToken mechanics are covered separately by RunLeaseFencingTests (S-07);
/// these tests deliberately stay on the product HITL contract.
/// </summary>
public sealed class TeamClarificationWorkflowResumeTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "onecode-tests", "team-clarification-resume", Guid.NewGuid().ToString("N"));

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Resume_AfterProcessRestart_DeliversExternalAnswer()
    {
        // Process 1: run the clarification workflow until it suspends on the RequestPort.
        var host1 = CreateHost();
        var runId = TeamRunId.NewId();
        var input = new TeamClarificationInput(runId.ToString(), "team-a", ["What is the target framework?"]);
        var config = CreateConfig();

        var first = await host1.RunAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);
        first.Durable.IsPending.Should().BeTrue("clarification must suspend before the user answers");
        first.PendingRequest.Should().NotBeNull();
        var pending = first.PendingRequest!;

        // host1 leaves scope: the DurableWorkflowHost released its Lease via `await using`,
        // the Registry record and Checkpoint are persisted on disk. A second host instance
        // pointing at the same directories simulates a new process resuming the run.
        var host2 = CreateHost();
        var response = new ExternalResponse(
            new RequestPortInfo(
                new TypeId(typeof(TeamClarificationInput)),
                new TypeId(typeof(TeamClarificationResponse)),
                pending.PortId),
            pending.RequestId,
            new PortableValue(new TeamClarificationResponse("net8.0")));

        var second = await host2.RunAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            externalResponse: response,
            ct: TestContext.Current.CancellationToken);

        second.Durable.IsPending.Should().BeFalse("the resumed run should reach a terminal output");
        second.Durable.Resumed.Should().BeTrue("the host must have restored from the persisted checkpoint");
        second.Answer.Should().Be("net8.0",
            "the external clarification answer must flow through to the sink executor output");
    }

    [Fact]
    public async Task Resume_PreservesRequestIdPortIdAndCommandId_AcrossRestart()
    {
        var host1 = CreateHost();
        var runId = TeamRunId.NewId();
        var input = new TeamClarificationInput(runId.ToString(), "team-a", ["Where is the spec?"]);
        var config = CreateConfig();

        var first = await host1.RunAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);
        var pendingBefore = first.PendingRequest!;
        pendingBefore.CommandId.Should().Be($"team/{runId}/clarification");

        // Simulate process restart: a second host instance reads the same persisted run.
        var host2 = CreateHost();
        var second = await host2.RunAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);

        // S-03 requirement: the same RequestInfoEvent must be re-emitted after restore, with
        // RequestId / PortId / CommandId unchanged so an externally persisted response still routes.
        second.Durable.IsPending.Should().BeTrue();
        second.PendingRequest.Should().NotBeNull();
        var pendingAfter = second.PendingRequest!;
        pendingAfter.RequestId.Should().Be(pendingBefore.RequestId);
        pendingAfter.PortId.Should().Be(pendingBefore.PortId);
        pendingAfter.CommandId.Should().Be(pendingBefore.CommandId);
    }

    [Fact]
    public async Task Resume_WithWrongRequestId_Throws()
    {
        var host1 = CreateHost();
        var runId = TeamRunId.NewId();
        var input = new TeamClarificationInput(runId.ToString(), "team-a", ["Question?"]);
        var config = CreateConfig();

        var first = await host1.RunAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);
        var pending = first.PendingRequest!;

        var host2 = CreateHost();
        var responseWithWrongRequestId = new ExternalResponse(
            new RequestPortInfo(
                new TypeId(typeof(TeamClarificationInput)),
                new TypeId(typeof(TeamClarificationResponse)),
                pending.PortId),
            "not-the-pending-request-id",
            new PortableValue(new TeamClarificationResponse("answer")));

        var act = async () => await host2.RunAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            externalResponse: responseWithWrongRequestId,
            ct: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "a response with a wrong RequestId must be rejected fail-closed, never silently routed");
    }

    [Fact]
    public async Task Resume_WithWrongPortId_Throws()
    {
        var host1 = CreateHost();
        var runId = TeamRunId.NewId();
        var input = new TeamClarificationInput(runId.ToString(), "team-a", ["Question?"]);
        var config = CreateConfig();

        var first = await host1.RunAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);
        var pending = first.PendingRequest!;

        var host2 = CreateHost();
        var responseWithWrongPortId = new ExternalResponse(
            new RequestPortInfo(
                new TypeId(typeof(TeamClarificationInput)),
                new TypeId(typeof(TeamClarificationResponse)),
                "not-the-pending-port-id"),
            pending.RequestId,
            new PortableValue(new TeamClarificationResponse("answer")));

        var act = async () => await host2.RunAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            externalResponse: responseWithWrongPortId,
            ct: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "a response with a wrong PortId must be rejected fail-closed");
    }

    [Fact]
    public async Task Resume_AfterTerminalState_Throws()
    {
        var host1 = CreateHost();
        var runId = TeamRunId.NewId();
        var input = new TeamClarificationInput(runId.ToString(), "team-a", ["Question?"]);
        var config = CreateConfig();

        var first = await host1.RunAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);
        var pending = first.PendingRequest!;

        // Complete the run end-to-end so it reaches a terminal state.
        var host2 = CreateHost();
        var correctResponse = new ExternalResponse(
            new RequestPortInfo(
                new TypeId(typeof(TeamClarificationInput)),
                new TypeId(typeof(TeamClarificationResponse)),
                pending.PortId),
            pending.RequestId,
            new PortableValue(new TeamClarificationResponse("final-answer")));
        var second = await host2.RunAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            externalResponse: correctResponse,
            ct: TestContext.Current.CancellationToken);
        second.Durable.IsPending.Should().BeFalse();

        // A third invocation against the now-terminal run must be rejected; duplicate responses
        // are not allowed to re-open a Completed run.
        var host3 = CreateHost();
        var act = async () => await host3.RunAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            externalResponse: correctResponse,
            ct: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "a terminal run must reject further responses — duplicate HITL responses must not re-open the run");
    }

    private TeamClarificationWorkflowHost CreateHost()
    {
        var registry = new JsonWorkflowRunRegistry(Path.Combine(_root, "registry"));
        var durableHost = new DurableWorkflowHost(
            registry,
            new WorkflowCheckpointStoreFactory(Path.Combine(_root, "checkpoints")),
            new WorkflowEventAdapter(),
            NullLogger<DurableWorkflowHost>.Instance);
        return new TeamClarificationWorkflowHost(
            durableHost,
            new TeamClarificationWorkflowCompiler(),
            registry);
    }

    private static TeamConfig CreateConfig(TeamOrchestrationMode mode = TeamOrchestrationMode.GroupChat)
        => new(
            "team-a",
            "C:/teams/team-a.yaml",
            [new TeamMember("lead-1", "lead", null), new TeamMember("executor-1", "executor", null)],
            MaxTurns: 10,
            mode);
}
