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
/// Product-level HITL recovery tests for the Team plan-approval workflow.
///
/// These tests verify S-03 product requirements on top of the MAF RequestPort mechanics that
/// are already covered by <see cref="TeamApprovalWorkflowTests"/>:
///   1. A workflow that suspends on the plan-approval RequestPort can be resumed from a
///      brand-new host instance pointing at the same on-disk Registry + Checkpoint store
///      (simulates a process restart), and the user's approve / deny decision flows through
///      to the terminal <see cref="TeamPlanApprovalOutput"/>.
///   2. RequestId, PortId and business CommandId stay identical across the restart.
///   3. Wrong RequestId / wrong PortId / post-terminal responses are rejected fail-closed.
///
/// The existing <c>TeamApprovalWorkflowTests</c> only exercises same-instance, same-generation
/// resume. These tests are the cross-process counterpart required by S-03.
/// </summary>
public sealed class TeamApprovalWorkflowResumeTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "onecode-tests", "team-approval-resume", Guid.NewGuid().ToString("N"));

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
    public async Task Resume_AfterProcessRestart_DeliversApproval()
    {
        var host1 = CreateHost();
        var runId = TeamRunId.NewId();
        var input = new TeamPlanApprovalInput(
            runId.ToString(), "team-a", "Implement feature", ["implement", "review"], ["build"]);
        var config = CreateConfig();

        var first = await host1.RunApprovalAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);
        first.Durable.IsPending.Should().BeTrue("approval gate must suspend until the user decides");
        first.ApprovalGranted.Should().BeNull();
        var pending = first.PendingRequest!;

        // Simulate process restart: a second host instance reads the same persisted run.
        var host2 = CreateHost();
        var approvalResponse = new ExternalResponse(
            new RequestPortInfo(
                new TypeId(typeof(TeamPlanApprovalInput)),
                new TypeId(typeof(TeamPlanApprovalDecision)),
                pending.PortId),
            pending.RequestId,
            new PortableValue(new TeamPlanApprovalDecision(Approved: true)));

        var second = await host2.RunApprovalAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            externalResponse: approvalResponse,
            ct: TestContext.Current.CancellationToken);

        second.Durable.IsPending.Should().BeFalse("the resumed run should reach a terminal decision");
        second.Durable.Resumed.Should().BeTrue("the host must have restored from the persisted checkpoint");
        second.ApprovalGranted.Should().BeTrue(
            "the user's approve decision must flow through to the terminal output");
    }

    [Fact]
    public async Task Resume_AfterProcessRestart_DeliversDenial()
    {
        var host1 = CreateHost();
        var runId = TeamRunId.NewId();
        var input = new TeamPlanApprovalInput(
            runId.ToString(), "team-a", "Implement feature", ["implement"], ["build"]);
        var config = CreateConfig();

        var first = await host1.RunApprovalAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);
        var pending = first.PendingRequest!;

        var host2 = CreateHost();
        var denialResponse = new ExternalResponse(
            new RequestPortInfo(
                new TypeId(typeof(TeamPlanApprovalInput)),
                new TypeId(typeof(TeamPlanApprovalDecision)),
                pending.PortId),
            pending.RequestId,
            new PortableValue(new TeamPlanApprovalDecision(Approved: false)));

        var second = await host2.RunApprovalAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            externalResponse: denialResponse,
            ct: TestContext.Current.CancellationToken);

        second.Durable.IsPending.Should().BeFalse();
        second.Durable.Resumed.Should().BeTrue();
        second.ApprovalGranted.Should().BeFalse(
            "the user's deny decision must flow through to the terminal output");
    }

    [Fact]
    public async Task Resume_PreservesRequestIdPortIdAndCommandId_AcrossRestart()
    {
        var host1 = CreateHost();
        var runId = TeamRunId.NewId();
        var input = new TeamPlanApprovalInput(
            runId.ToString(), "team-a", "Implement feature", ["a"], ["build"]);
        var config = CreateConfig();

        var first = await host1.RunApprovalAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);
        var pendingBefore = first.PendingRequest!;
        pendingBefore.CommandId.Should().Be($"team/{runId}/approve");
        pendingBefore.PortId.Should().Be("team-plan-approval-v1");

        // Simulate process restart: a second host instance re-emits the same pending request.
        var host2 = CreateHost();
        var second = await host2.RunApprovalAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);

        second.Durable.IsPending.Should().BeTrue();
        second.PendingRequest.Should().NotBeNull();
        var pendingAfter = second.PendingRequest!;
        pendingAfter.RequestId.Should().Be(pendingBefore.RequestId,
            "RequestId must be stable across restart so an externally persisted response still routes");
        pendingAfter.PortId.Should().Be(pendingBefore.PortId);
        pendingAfter.CommandId.Should().Be(pendingBefore.CommandId);
    }

    [Fact]
    public async Task Resume_WithWrongRequestId_Throws()
    {
        var host1 = CreateHost();
        var runId = TeamRunId.NewId();
        var input = new TeamPlanApprovalInput(
            runId.ToString(), "team-a", "Implement feature", ["a"], ["build"]);
        var config = CreateConfig();

        var first = await host1.RunApprovalAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);
        var pending = first.PendingRequest!;

        var host2 = CreateHost();
        var responseWithWrongRequestId = new ExternalResponse(
            new RequestPortInfo(
                new TypeId(typeof(TeamPlanApprovalInput)),
                new TypeId(typeof(TeamPlanApprovalDecision)),
                pending.PortId),
            "not-the-pending-request-id",
            new PortableValue(new TeamPlanApprovalDecision(Approved: true)));

        var act = async () => await host2.RunApprovalAsync(
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
        var input = new TeamPlanApprovalInput(
            runId.ToString(), "team-a", "Implement feature", ["a"], ["build"]);
        var config = CreateConfig();

        var first = await host1.RunApprovalAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);
        var pending = first.PendingRequest!;

        var host2 = CreateHost();
        var responseWithWrongPortId = new ExternalResponse(
            new RequestPortInfo(
                new TypeId(typeof(TeamPlanApprovalInput)),
                new TypeId(typeof(TeamPlanApprovalDecision)),
                "not-the-pending-port-id"),
            pending.RequestId,
            new PortableValue(new TeamPlanApprovalDecision(Approved: true)));

        var act = async () => await host2.RunApprovalAsync(
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
        var input = new TeamPlanApprovalInput(
            runId.ToString(), "team-a", "Implement feature", ["a"], ["build"]);
        var config = CreateConfig();

        var first = await host1.RunApprovalAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);
        var pending = first.PendingRequest!;

        // Complete the run end-to-end so it reaches a terminal state.
        var host2 = CreateHost();
        var correctResponse = new ExternalResponse(
            new RequestPortInfo(
                new TypeId(typeof(TeamPlanApprovalInput)),
                new TypeId(typeof(TeamPlanApprovalDecision)),
                pending.PortId),
            pending.RequestId,
            new PortableValue(new TeamPlanApprovalDecision(Approved: true)));
        var second = await host2.RunApprovalAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            externalResponse: correctResponse,
            ct: TestContext.Current.CancellationToken);
        second.Durable.IsPending.Should().BeFalse();
        second.ApprovalGranted.Should().BeTrue();

        // A third invocation against the now-terminal run must be rejected; duplicate responses
        // are not allowed to re-open a Completed run or flip its decision.
        var host3 = CreateHost();
        var act = async () => await host3.RunApprovalAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            externalResponse: correctResponse,
            ct: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "a terminal run must reject further responses — duplicate HITL responses must not re-open or flip the run");
    }

    private TeamApprovalWorkflowHost CreateHost()
    {
        var registry = new JsonWorkflowRunRegistry(Path.Combine(_root, "registry"));
        var durableHost = new DurableWorkflowHost(
            registry,
            new WorkflowCheckpointStoreFactory(Path.Combine(_root, "checkpoints")),
            new WorkflowEventAdapter(),
            NullLogger<DurableWorkflowHost>.Instance);
        return new TeamApprovalWorkflowHost(
            durableHost,
            new TeamApprovalWorkflowCompiler(),
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
