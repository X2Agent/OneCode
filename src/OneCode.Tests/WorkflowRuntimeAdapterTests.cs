using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using OneCode.App.Services.Agent;
using OneCode.Core.Workflows;

namespace OneCode.Tests;

public sealed class WorkflowRuntimeAdapterTests
{
    [Fact]
    public void PendingRequestEvent_PreservesDurableRoutingIdentity()
    {
        var request = new ExternalRequest(
            new RequestPortInfo(new TypeId(typeof(string)), new TypeId(typeof(bool)), "approval-port"),
            "request-1",
            new PortableValue("approve"));
        var adapter = new WorkflowEventAdapter();

        var mapped = adapter.Adapt("run-1", new RequestInfoEvent(request), "command-1");

        mapped.Should().BeOfType<WorkflowRuntimeEvent.PendingRequest>();
        var pending = (WorkflowRuntimeEvent.PendingRequest)mapped;
        pending.ExecutorId.Should().BeNull();
        pending.Request.RequestId.Should().Be(request.RequestId);
        pending.Request.PortId.Should().Be("approval-port");
        pending.Request.CommandId.Should().Be("command-1");
    }

    [Fact]
    public void UnknownLifecycleEvent_UsesStableTypeNameWithoutInventingExecutor()
    {
        var adapter = new WorkflowEventAdapter();
        var request = new ExternalRequest(
            new RequestPortInfo(new TypeId(typeof(string)), new TypeId(typeof(bool)), "port"),
            "request-2",
            new PortableValue("approve"));

        var mapped = adapter.Adapt("run-2", new RequestInfoEvent(request), "command-2");

        mapped.RunId.Should().Be("run-2");
        mapped.ExecutorId.Should().BeNull();
    }
}
