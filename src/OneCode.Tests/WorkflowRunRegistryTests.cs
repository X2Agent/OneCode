using OneCode.App.Services.Agent;
using OneCode.Core.Workflows;
using OneCode.Infrastructure.Workflows;

namespace OneCode.Tests;

public sealed class WorkflowRunRegistryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "onecode-tests", "workflow-run-registry", Guid.NewGuid().ToString("N"));

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
    public async Task Acquire_Reconcile_TakeoverAndComplete_PreservesRuntimeTruth()
    {
        var registry = CreateRegistry();
        var registration = new WorkflowRunRegistration("run-1", "build", "definition-v1");

        await using (var first = await registry.TryAcquireAsync(registration, TestContext.Current.CancellationToken))
        {
            first.Should().NotBeNull();
            first!.FencingToken.Should().Be(1);
            await registry.ReconcileCheckpointAsync(
                registration.RunId,
                first.FencingToken,
                "checkpoint-1",
                TestContext.Current.CancellationToken);
        }

        await using var second = await registry.TryAcquireAsync(registration, TestContext.Current.CancellationToken);
        second.Should().NotBeNull();
        second!.FencingToken.Should().Be(2);

        var stale = () => registry.ReconcileCheckpointAsync(
            registration.RunId,
            1,
            "stale-checkpoint",
            TestContext.Current.CancellationToken);
        await stale.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Stale*");

        await registry.CompleteAsync(
            registration.RunId,
            second.FencingToken,
            WorkflowRunState.Completed,
            TestContext.Current.CancellationToken);
        var record = await registry.LoadAsync(registration.RunId, TestContext.Current.CancellationToken);
        record.Should().NotBeNull();
        record!.CheckpointId.Should().Be("checkpoint-1");
        record.State.Should().Be(WorkflowRunState.Completed);
        record.FencingToken.Should().Be(2);
    }

    [Fact]
    public async Task ConcurrentAcquire_OnlyOneLeaseOwnsRun()
    {
        var firstRegistry = CreateRegistry();
        var secondRegistry = CreateRegistry();
        var registration = new WorkflowRunRegistration("run-contended", "goal", "definition-v1");

        await using var first = await firstRegistry.TryAcquireAsync(registration, TestContext.Current.CancellationToken);
        await using var second = await secondRegistry.TryAcquireAsync(registration, TestContext.Current.CancellationToken);

        first.Should().NotBeNull();
        second.Should().BeNull();
    }

    [Fact]
    public async Task ActiveScan_IgnoresCheckpointOrphansAndTerminalRuns()
    {
        var registry = CreateRegistry();
        var active = new WorkflowRunRegistration("run-active", "team", "definition-a");
        var completed = new WorkflowRunRegistration("run-completed", "team", "definition-b");

        await using var activeLease = await registry.TryAcquireAsync(active, TestContext.Current.CancellationToken);
        await using var completedLease = await registry.TryAcquireAsync(completed, TestContext.Current.CancellationToken);
        await registry.CompleteAsync(
            completed.RunId,
            completedLease!.FencingToken,
            WorkflowRunState.Completed,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "orphan.checkpoint"),
            "orphan",
            TestContext.Current.CancellationToken);

        var records = await registry.LoadActiveAsync(TestContext.Current.CancellationToken);

        records.Should().ContainSingle(record => record.RunId == active.RunId);
        records.Should().NotContain(record => record.RunId == completed.RunId);
    }

    [Fact]
    public async Task DefinitionMismatchAndTerminalReacquire_FailClosed()
    {
        var registry = CreateRegistry();
        var registration = new WorkflowRunRegistration("run-definition", "build", "definition-v1");
        await using (var firstLease = await registry.TryAcquireAsync(registration, TestContext.Current.CancellationToken))
            firstLease.Should().NotBeNull();

        var mismatch = () => registry.TryAcquireAsync(
            registration with { DefinitionHash = "definition-v2" },
            TestContext.Current.CancellationToken);
        await mismatch.Should().ThrowAsync<InvalidOperationException>().WithMessage("*definition*");

        await using var lease = await registry.TryAcquireAsync(registration, TestContext.Current.CancellationToken);
        await registry.CompleteAsync(
            registration.RunId,
            lease!.FencingToken,
            WorkflowRunState.Failed,
            TestContext.Current.CancellationToken);
        await lease.DisposeAsync();
        var terminal = () => registry.TryAcquireAsync(registration, TestContext.Current.CancellationToken);
        await terminal.Should().ThrowAsync<InvalidOperationException>().WithMessage("*terminal*");
    }

    [Fact]
    public async Task BeginGeneration_NewGenerationClearsCheckpointAndPendingRequest()
    {
        var registry = CreateRegistry();
        var registration = new WorkflowRunRegistration("run-generation", "build", "definition-v1");
        await using (var firstLease = await registry.TryAcquireAsync(registration, TestContext.Current.CancellationToken))
        {
            var first = await registry.BeginGenerationAsync(
                registration.RunId,
                firstLease!.FencingToken,
                1,
                TestContext.Current.CancellationToken);
            await registry.ReconcileCheckpointAsync(
                registration.RunId,
                first.FencingToken,
                "checkpoint-1",
                TestContext.Current.CancellationToken);
            await registry.RegisterPendingRequestAsync(
                registration.RunId,
                first.FencingToken,
                new WorkflowPendingRequest("request-1", "port-1", "command-1", DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
        }

        await using var secondLease = await registry.TryAcquireAsync(registration, TestContext.Current.CancellationToken);
        var second = await registry.BeginGenerationAsync(
            registration.RunId,
            secondLease!.FencingToken,
            2,
            TestContext.Current.CancellationToken);

        second.FencingToken.Should().BeGreaterThan(1);
        second.ExecutionGeneration.Should().Be(2);
        second.CheckpointId.Should().BeNull();
        second.PendingRequest.Should().BeNull();

        var stale = () => registry.BeginGenerationAsync(
            registration.RunId,
            second.FencingToken,
            1,
            TestContext.Current.CancellationToken);
        await stale.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Stale*generation*");
    }

    [Fact]
    public async Task PendingRequest_IsDurableIdempotentAndFenced()
    {
        var registry = CreateRegistry();
        var registration = new WorkflowRunRegistration("run-request", "build", "definition-v1");
        await using var lease = await registry.TryAcquireAsync(registration, TestContext.Current.CancellationToken);
        var request = new WorkflowPendingRequest(
            "request-1",
            "approval-port",
            "command-1",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5));

        await registry.RegisterPendingRequestAsync(
            registration.RunId,
            lease!.FencingToken,
            request,
            TestContext.Current.CancellationToken);
        await registry.RegisterPendingRequestAsync(
            registration.RunId,
            lease.FencingToken,
            request,
            TestContext.Current.CancellationToken);

        var mismatch = () => registry.ConsumePendingRequestAsync(
            registration.RunId,
            lease.FencingToken,
            request with { CommandId = "command-wrong" },
            TestContext.Current.CancellationToken);
        await mismatch.Should().ThrowAsync<InvalidOperationException>().WithMessage("*identity*");

        await registry.ConsumePendingRequestAsync(
            registration.RunId,
            lease.FencingToken,
            request,
            TestContext.Current.CancellationToken);
        var record = await registry.LoadAsync(registration.RunId, TestContext.Current.CancellationToken);
        record!.PendingRequest.Should().BeNull();
    }

    [Fact]
    public async Task ExpiredPendingRequest_FailsClosed()
    {
        var registry = CreateRegistry();
        var registration = new WorkflowRunRegistration("run-expired", "goal", "definition-v1");
        await using var lease = await registry.TryAcquireAsync(registration, TestContext.Current.CancellationToken);
        var request = new WorkflowPendingRequest(
            "request-expired",
            "approval-port",
            "command-expired",
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow.AddMinutes(-1));
        await registry.RegisterPendingRequestAsync(
            registration.RunId,
            lease!.FencingToken,
            request,
            TestContext.Current.CancellationToken);

        var consume = () => registry.ConsumePendingRequestAsync(
            registration.RunId,
            lease.FencingToken,
            request,
            TestContext.Current.CancellationToken);
        await consume.Should().ThrowAsync<InvalidOperationException>().WithMessage("*expired*");
    }

    [Fact]
    public async Task CheckpointFactory_RequiresActiveRegistryRecordAndExclusiveOwnership()
    {
        var registry = CreateRegistry();
        var registration = new WorkflowRunRegistration("run-checkpoint", "build", "definition-v1");
        await using var lease = await registry.TryAcquireAsync(registration, TestContext.Current.CancellationToken);
        var record = await registry.LoadAsync(registration.RunId, TestContext.Current.CancellationToken);
        var factory = new WorkflowCheckpointStoreFactory(Path.Combine(_root, "checkpoints"));

        await using (var handle = await factory.OpenAsync(
                         record!,
                         new System.Text.Json.JsonSerializerOptions(),
                         TestContext.Current.CancellationToken))
        {
            handle.RunId.Should().Be(registration.RunId);
            handle.Manager.Should().NotBeNull();

            var contended = () => factory.OpenAsync(
                record!,
                new System.Text.Json.JsonSerializerOptions(),
                TestContext.Current.CancellationToken);
            await contended.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already in use*");
        }

        await using var reopened = await factory.OpenAsync(
            record!,
            new System.Text.Json.JsonSerializerOptions(),
            TestContext.Current.CancellationToken);
        reopened.RunId.Should().Be(registration.RunId);
    }

    private JsonWorkflowRunRegistry CreateRegistry() => new(_root);
}
