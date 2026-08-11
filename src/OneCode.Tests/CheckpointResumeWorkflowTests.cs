using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace OneCode.Tests;

/// <summary>
/// End-to-end tests verifying that InMemory checkpointing allows
/// resuming a workflow from a previously captured checkpoint.
///
/// These tests cover the in-memory, same-instance restore mechanics used by the current Team path:
///   1. Run workflow with CheckpointManager.CreateInMemory to capture superstep checkpoints.
///   2. Dispose StreamingRun to release Workflow ownership.
///   3. Resume from a selected checkpoint in the same process.
///
/// They do not establish a product-level same-instance requirement. Durable new-instance recovery is
/// covered separately by <see cref="CrossProcessCheckpointRecoveryTests"/>, which uses
/// <c>FileSystemJsonCheckpointStore</c> and two independent processes.
/// <see cref="InProcessExecution.Lockstep"/> is used here only for deterministic test behavior.
/// </summary>
public sealed class CheckpointResumeWorkflowTests
{
    /// <summary>
    /// Drain a streaming run to completion, collecting <see cref="AgentResponseEvent"/> texts.
    /// Uses a timeout to prevent indefinite hangs in case of unexpected MAF behavior.
    /// </summary>
    private static async Task<List<string>> DrainStreamAsync(
        IAsyncEnumerable<WorkflowEvent> stream, CancellationToken ct)
    {
        var outputs = new List<string>();
        await foreach (var evt in stream.ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (evt is AgentResponseEvent resp && !string.IsNullOrEmpty(resp.Response?.Text))
                outputs.Add(resp.Response.Text);
        }
        return outputs;
    }

    // Checkpoint capture

    [Fact]
    public async Task RunStreamingAsync_WithInMemoryCheckpoint_CapturesAtLeastOneCheckpoint()
    {
        var ct = TestContext.Current.CancellationToken;

        var agent = TestAgents.CreateCountingAgent("solo-agent");
        var workflow = AgentWorkflowBuilder.BuildSequential("single", new[] { agent });

        var checkpointManager = CheckpointManager.CreateInMemory();
        var env = InProcessExecution.Lockstep.WithCheckpointing(checkpointManager);

        var sessionId = $"capture-{Guid.NewGuid():N}";
        var input = new ChatMessage(ChatRole.User, "start");

        var run = await env.RunStreamingAsync(workflow, input, sessionId, ct);
        await DrainStreamAsync(run.WatchStreamAsync(ct), ct);

        // After draining, at least one checkpoint should exist (superstep boundary).
        run.Checkpoints.Should().NotBeEmpty(
            "InMemory checkpointing should capture at least one superstep boundary");
        run.LastCheckpoint.Should().NotBeNull();
        run.LastCheckpoint!.SessionId.Should().Be(sessionId);
        run.LastCheckpoint.CheckpointId.Should().NotBeNullOrEmpty();

        await run.DisposeAsync();
    }

    // Resume from LastCheckpoint

    [Fact]
    public async Task ResumeStreamingAsync_AfterDispose_ContinuesExecutionWithoutError()
    {
        var ct = TestContext.Current.CancellationToken;

        var agent = TestAgents.CreateCountingAgent("resume-agent");
        var workflow = AgentWorkflowBuilder.BuildSequential("resume-test", new[] { agent });

        var checkpointManager = CheckpointManager.CreateInMemory();
        var env = InProcessExecution.Lockstep.WithCheckpointing(checkpointManager);

        var sessionId = $"resume-{Guid.NewGuid():N}";
        var input = new ChatMessage(ChatRole.User, "start");

        // Phase 1: Run to completion, capture checkpoint
        var run1 = await env.RunStreamingAsync(workflow, input, sessionId, ct);
        await DrainStreamAsync(run1.WatchStreamAsync(ct), ct);

        run1.Checkpoints.Should().NotBeEmpty();
        run1.LastCheckpoint.Should().NotBeNull();
        var lastCheckpoint = run1.LastCheckpoint!;
        lastCheckpoint.SessionId.Should().Be(sessionId);

        // MAF requirement: dispose the run to release Workflow ownership
        await run1.DisposeAsync();

        // Phase 2: Resume from the captured checkpoint using the SAME workflow
        var run2 = await env.ResumeStreamingAsync(workflow, lastCheckpoint, ct);
        await DrainStreamAsync(run2.WatchStreamAsync(ct), ct);

        // The resumed run should have its own checkpoint(s)
        run2.Checkpoints.Should().NotBeEmpty(
            "resumed workflow should produce new checkpoints at superstep boundaries");
        run2.LastCheckpoint.Should().NotBeNull();

        await run2.DisposeAsync();
    }

    // Checkpoint metadata integrity

    [Fact]
    public async Task CheckpointInfo_ContainsValidSessionAndCheckpointIds()
    {
        var ct = TestContext.Current.CancellationToken;

        var agent = TestAgents.CreateCountingAgent("integrity-agent");
        var workflow = AgentWorkflowBuilder.BuildSequential("integrity", new[] { agent });

        var checkpointManager = CheckpointManager.CreateInMemory();
        var env = InProcessExecution.Lockstep.WithCheckpointing(checkpointManager);

        var sessionId = $"integrity-{Guid.NewGuid():N}";
        var input = new ChatMessage(ChatRole.User, "start");

        var run = await env.RunStreamingAsync(workflow, input, sessionId, ct);
        await DrainStreamAsync(run.WatchStreamAsync(ct), ct);

        foreach (var cp in run.Checkpoints)
        {
            cp.SessionId.Should().Be(sessionId,
                "every checkpoint must carry the session ID it belongs to");
            cp.CheckpointId.Should().NotBeNullOrEmpty(
                "every checkpoint must have a non-empty checkpoint ID");
        }

        run.LastCheckpoint.Should().NotBeNull();
        run.LastCheckpoint!.CheckpointId.Should().NotBeNullOrEmpty();

        await run.DisposeAsync();
    }

    // Resume from specific checkpoint

    [Fact]
    public async Task ResumeStreamingAsync_FromFirstCheckpoint_SucceedsAfterDispose()
    {
        var ct = TestContext.Current.CancellationToken;

        var agent = TestAgents.CreateCountingAgent("specific-agent");
        var workflow = AgentWorkflowBuilder.BuildSequential("specific", new[] { agent });

        var checkpointManager = CheckpointManager.CreateInMemory();
        var env = InProcessExecution.Lockstep.WithCheckpointing(checkpointManager);

        var sessionId = $"specific-{Guid.NewGuid():N}";
        var input = new ChatMessage(ChatRole.User, "start");

        var run1 = await env.RunStreamingAsync(workflow, input, sessionId, ct);
        await DrainStreamAsync(run1.WatchStreamAsync(ct), ct);

        run1.Checkpoints.Should().NotBeEmpty();
        var firstCheckpoint = run1.Checkpoints[0];
        firstCheckpoint.SessionId.Should().Be(sessionId);
        firstCheckpoint.CheckpointId.Should().NotBeNullOrEmpty();

        await run1.DisposeAsync();

        // Resume from the first checkpoint
        var run2 = await env.ResumeStreamingAsync(workflow, firstCheckpoint, ct);
        await DrainStreamAsync(run2.WatchStreamAsync(ct), ct);

        run2.LastCheckpoint.Should().NotBeNull(
            "resumed run should advance the checkpoint cursor");

        await run2.DisposeAsync();
    }

    // Workflow ownership enforcement

    [Fact]
    public async Task ResumeStreamingAsync_WithoutDispose_ThrowsInvalidOperationException()
    {
        // This test documents the MAF ownership constraint:
        // if you try to resume without disposing the previous run, MAF throws.
        var ct = TestContext.Current.CancellationToken;

        var agent = TestAgents.CreateCountingAgent("ownership-agent");
        var workflow = AgentWorkflowBuilder.BuildSequential("ownership", new[] { agent });

        var checkpointManager = CheckpointManager.CreateInMemory();
        var env = InProcessExecution.Lockstep.WithCheckpointing(checkpointManager);

        var sessionId = $"ownership-{Guid.NewGuid():N}";
        var input = new ChatMessage(ChatRole.User, "start");

        var run1 = await env.RunStreamingAsync(workflow, input, sessionId, ct);
        await DrainStreamAsync(run1.WatchStreamAsync(ct), ct);

        // Attempt to resume WITHOUT disposing run1 first
        var act = async () =>
        {
            var run2 = await env.ResumeStreamingAsync(workflow, run1.LastCheckpoint!, ct);
            await DrainStreamAsync(run2.WatchStreamAsync(ct), ct);
        };

        await act.Should().ThrowAsync<InvalidOperationException>(
            "MAF enforces workflow ownership — must dispose previous run before resuming");

        // Cleanup
        await run1.DisposeAsync();
    }
}
