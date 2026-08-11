using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Services.Agent;
using OneCode.Core.Workflows;
using OneCode.Infrastructure.Workflows;

namespace OneCode.Tests;

public sealed class DurableWorkflowHostTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "onecode-tests", "durable-workflow-host", Guid.NewGuid().ToString("N"));

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
    public async Task CompletedWorkflow_ReconcilesCheckpointAndTerminalState()
    {
        var host = CreateHost(out var registry);
        var workflow = BuildCompletedWorkflow();
        var registration = new WorkflowRunRegistration("host-complete", "test", "host-complete-v1");

        var result = await host.RunAsync(
            registration,
            workflow,
            new HostInput("seed"),
            "command-complete",
            new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);

        result.Resumed.Should().BeFalse();
        result.IsPending.Should().BeFalse();
        result.Run.State.Should().Be(WorkflowRunState.Completed);
        result.Run.CheckpointId.Should().NotBeNullOrWhiteSpace();
        result.Events.Should().Contain(item => item is WorkflowRuntimeEvent.Output);
        (await registry.LoadActiveAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task PendingWorkflow_NewWorkflowInstanceResumesFromReconciledCheckpoint()
    {
        var host = CreateHost(out var registry);
        var registration = new WorkflowRunRegistration("host-pending", "test", "host-pending-v1");

        var first = await host.RunAsync(
            registration,
            BuildPendingWorkflow(),
            new HostInput("seed"),
            "command-pending",
            new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);

        first.Resumed.Should().BeFalse();
        first.IsPending.Should().BeTrue();
        first.Run.State.Should().Be(WorkflowRunState.Active);
        first.Run.CheckpointId.Should().NotBeNullOrWhiteSpace();
        first.Run.PendingRequest.Should().NotBeNull();

        var second = await host.RunAsync(
            registration,
            BuildPendingWorkflow(),
            new HostInput("ignored-on-resume"),
            "command-pending",
            new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);

        second.Resumed.Should().BeTrue();
        second.IsPending.Should().BeTrue();
        second.Run.FencingToken.Should().BeGreaterThan(first.Run.FencingToken);
        second.Run.PendingRequest!.RequestId.Should().Be(first.Run.PendingRequest!.RequestId);
        second.Run.PendingRequest.PortId.Should().Be(first.Run.PendingRequest.PortId);
        second.Run.PendingRequest.CommandId.Should().Be(first.Run.PendingRequest.CommandId);
        (await registry.LoadActiveAsync(TestContext.Current.CancellationToken))
            .Should().ContainSingle(record => record.RunId == registration.RunId);
    }

    private DurableWorkflowHost CreateHost(out JsonWorkflowRunRegistry registry)
    {
        registry = new JsonWorkflowRunRegistry(Path.Combine(_root, "registry"));
        return new DurableWorkflowHost(
            registry,
            new WorkflowCheckpointStoreFactory(Path.Combine(_root, "checkpoints")),
            new WorkflowEventAdapter(),
            NullLogger<DurableWorkflowHost>.Instance);
    }

    private static Workflow BuildCompletedWorkflow()
    {
        var executor = new HostCompleteExecutor("host-complete-executor-v1");
        return new WorkflowBuilder(executor)
            .WithName("host-complete-workflow-v1")
            .WithOutputFrom(executor)
            .Build(validateOrphans: true);
    }

    private static Workflow BuildPendingWorkflow()
    {
        var start = new HostStartExecutor("host-start-executor-v1");
        var port = RequestPort.Create<HostApprovalRequest, HostApproval>("host-approval-port-v1");
        var request = port.BindAsExecutor();
        var finish = new HostFinishExecutor("host-finish-executor-v1");
        return new WorkflowBuilder(start)
            .WithName("host-pending-workflow-v1")
            .AddEdge(start, request, "host-start-request-edge-v1", false)
            .AddEdge(request, finish, "host-request-finish-edge-v1", false)
            .WithOutputFrom(finish)
            .Build(validateOrphans: true);
    }

    private sealed class HostCompleteExecutor(string id) : Executor<HostInput, HostOutput>(id)
    {
        public override ValueTask<HostOutput> HandleAsync(
            HostInput message,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new HostOutput(message.Value + "-complete"));
    }

    private sealed class HostStartExecutor(string id) : Executor<HostInput, HostApprovalRequest>(id)
    {
        public override ValueTask<HostApprovalRequest> HandleAsync(
            HostInput message,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new HostApprovalRequest(message.Value));
    }

    private sealed class HostFinishExecutor(string id) : Executor<HostApproval, HostOutput>(id)
    {
        public override ValueTask<HostOutput> HandleAsync(
            HostApproval message,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new HostOutput(message.Approved ? "approved" : "rejected"));
    }

    private sealed record HostInput(string Value);
    private sealed record HostApprovalRequest(string Value);
    private sealed record HostApproval(bool Approved);
    private sealed record HostOutput(string Value);
}
