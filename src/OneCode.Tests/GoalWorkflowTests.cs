using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Services.Agent;
using OneCode.App.Services.GoalMode;
using OneCode.Core.Domain;
using OneCode.Core.Goals;
using OneCode.Core.Workflows;
using OneCode.Infrastructure.Goals;
using OneCode.Infrastructure.Workflows;

namespace OneCode.Tests;

public sealed class GoalWorkflowTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onecode-goal-workflow-tests", Guid.NewGuid().ToString("N"));

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
    public async Task DynamicLoop_UsesStableExecutorsAndProcessesInsertedStep()
    {
        var runtime = new DynamicRuntime();
        var run = CreateRun("model-a", "prompt-a", "tools-a");
        var compiler = new GoalWorkflowCompiler();
        var definition = compiler.Compile(run, "model-a", "prompt-a", "tools-a", runtime, new JsonSerializerOptions());
        var host = CreateHost();

        var result = await host.RunAsync(
            definition.Registration,
            definition.Workflow,
            definition.Input,
            "goal-command-v1",
            new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);

        result.Run.State.Should().Be(WorkflowRunState.Completed);
        runtime.PlanCalls.Should().Be(1);
        runtime.StepCalls.Should().Be(3, "the first step dynamically inserted one additional step");
        runtime.CompletionCalls.Should().Be(1);
        var output = result.Events.OfType<WorkflowRuntimeEvent.Output>()
            .Select(item => item.Value)
            .OfType<GoalWorkflowOutput>()
            .Single();
        output.CompletedCount.Should().Be(3);
        output.FailedCount.Should().Be(0);
    }

    [Fact]
    public async Task GoalHost_ClaimsBusinessRunWithWorkflowFencingToken()
    {
        var runtime = new DynamicRuntime();
        var run = CreateRun("model-a", "prompt-a", "tools-a");
        var goalStore = new JsonGoalRunStore(Path.Combine(_root, "goal-runs"));
        await goalStore.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var registry = new JsonWorkflowRunRegistry(Path.Combine(_root, "host-registry"));
        var durableHost = new DurableWorkflowHost(
            registry,
            new WorkflowCheckpointStoreFactory(Path.Combine(_root, "host-checkpoints")),
            new WorkflowEventAdapter(),
            NullLogger<DurableWorkflowHost>.Instance);
        var host = new GoalWorkflowHost(durableHost, new GoalWorkflowCompiler(), goalStore, registry);

        var result = await host.RunAsync(
            await goalStore.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken) ?? throw new InvalidOperationException(),
            "model-a",
            "prompt-a",
            "tools-a",
            runtime,
            new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);

        result.Durable.Run.State.Should().Be(WorkflowRunState.Completed);
        var claimed = await goalStore.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);
        claimed!.WorkflowFencingToken.Should().Be(result.Durable.Run.FencingToken);
        runtime.BoundFencingToken.Should().Be(result.Durable.Run.FencingToken);
        claimed.State.Should().Be(GoalRunState.Planning, "MAF completion alone cannot complete the business aggregate");
        var stale = () => goalStore.SaveFencedAsync(
            claimed with { State = GoalRunState.Executing },
            claimed.Version,
            claimed.WorkflowFencingToken!.Value + 1,
            TestContext.Current.CancellationToken);
        await stale.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Stale*");
    }

    [Fact]
    public async Task GoalHost_PausedRunStartsNewGenerationInsteadOfResumingTerminalCheckpoint()
    {
        var run = CreateRun("model-a", "prompt-a", "tools-a");
        var goalStore = new JsonGoalRunStore(Path.Combine(_root, "paused-goal-runs"));
        await goalStore.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        var registry = new JsonWorkflowRunRegistry(Path.Combine(_root, "paused-registry"));
        var durableHost = new DurableWorkflowHost(
            registry,
            new WorkflowCheckpointStoreFactory(Path.Combine(_root, "paused-checkpoints")),
            new WorkflowEventAdapter(),
            NullLogger<DurableWorkflowHost>.Instance);
        var host = new GoalWorkflowHost(durableHost, new GoalWorkflowCompiler(), goalStore, registry);
        var pausedRuntime = new DynamicRuntime(GoalRunState.Paused);

        var first = await host.RunNextAsync(
            await goalStore.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken) ?? throw new InvalidOperationException(),
            "model-a",
            "prompt-a",
            "tools-a",
            pausedRuntime,
            new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);

        first.Durable.Run.State.Should().Be(WorkflowRunState.Active);
        first.Durable.Run.ExecutionGeneration.Should().Be(1);
        first.Durable.Run.CheckpointId.Should().NotBeNullOrWhiteSpace();
        var secondRuntime = new DynamicRuntime();
        var second = await host.RunNextAsync(
            await goalStore.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken) ?? throw new InvalidOperationException(),
            "model-a",
            "prompt-a",
            "tools-a",
            secondRuntime,
            new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);

        second.Durable.Run.State.Should().Be(WorkflowRunState.Completed);
        second.Durable.Run.ExecutionGeneration.Should().Be(2);
        second.Durable.Resumed.Should().BeFalse("a new generation clears the previous terminal checkpoint");
        secondRuntime.PlanCalls.Should().Be(1, "the workflow is rebuilt from GoalRun business facts");
        second.Durable.Run.FencingToken.Should().BeGreaterThan(first.Durable.Run.FencingToken);
    }

    [Fact]
    public void DefinitionHash_IsStableForBusinessPlanChangesAndSensitiveToSecurityInputs()
    {
        var baseline = CreateRun("model-a", "prompt-a", "tools-a");
        var changedPlan = baseline with
        {
            Plan = [Step(99, GoalStepState.Completed)],
            Executions =
            [
                new GoalStepExecutionEvidence(99, GoalStepState.Completed, 1, 2, 3, "ok", "ok", [], [], [], []),
            ],
        };

        var first = GoalWorkflowCompiler.ComputeDefinitionHash(baseline, "model-a", "prompt-a", "tools-a");
        GoalWorkflowCompiler.ComputeDefinitionHash(changedPlan, "model-a", "prompt-a", "tools-a").Should().Be(first);
        GoalWorkflowCompiler.ComputeDefinitionHash(baseline, "model-b", "prompt-a", "tools-a").Should().NotBe(first);
        GoalWorkflowCompiler.ComputeDefinitionHash(baseline, "model-a", "prompt-b", "tools-a").Should().NotBe(first);
        GoalWorkflowCompiler.ComputeDefinitionHash(baseline, "model-a", "prompt-a", "tools-b").Should().NotBe(first);
        GoalWorkflowCompiler.ComputeDefinitionHash(
            baseline with { WorkspaceFingerprint = "workspace-b" }, "model-a", "prompt-a", "tools-a").Should().NotBe(first);
    }

    [Fact]
    public void DefinitionHash_IsSensitiveToSerializerOptions()
    {
        var baseline = CreateRun("model-a", "prompt-a", "tools-a");

        var first = GoalWorkflowCompiler.ComputeDefinitionHash(baseline, "model-a", "prompt-a", "tools-a");
        // null 与显式 default options 是不同的序列化契约表达 → hash 不同；
        // 生产调用恒传非空 options，只要同配置则 hash 稳定。
        GoalWorkflowCompiler.ComputeDefinitionHash(
            baseline, "model-a", "prompt-a", "tools-a", new JsonSerializerOptions()).Should().NotBe(first);
        GoalWorkflowCompiler.ComputeDefinitionHash(
            baseline, "model-a", "prompt-a", "tools-a",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            .Should().NotBe(first);
        GoalWorkflowCompiler.ComputeDefinitionHash(
                baseline, "model-a", "prompt-a", "tools-a", new JsonSerializerOptions())
            .Should().Be(GoalWorkflowCompiler.ComputeDefinitionHash(
                baseline, "model-a", "prompt-a", "tools-a", new JsonSerializerOptions()));
    }

    private DurableWorkflowHost CreateHost()
    {
        var registry = new JsonWorkflowRunRegistry(Path.Combine(_root, "registry"));
        return new DurableWorkflowHost(
            registry,
            new WorkflowCheckpointStoreFactory(Path.Combine(_root, "checkpoints")),
            new WorkflowEventAdapter(),
            NullLogger<DurableWorkflowHost>.Instance);
    }

    private static GoalRun CreateRun(string modelId, string promptHash, string toolHash)
    {
        var run = new GoalRun
        {
            Id = GoalRunId.New(),
            SessionId = SessionId.NewId(),
            Goal = "Complete a dynamic goal.",
            WorkingDirectory = Path.GetTempPath(),
            WorkspaceFingerprint = "workspace-a",
            DefinitionHash = "pending",
            Plan = [],
        };
        return run with
        {
            DefinitionHash = GoalWorkflowCompiler.ComputeDefinitionHash(
                run, modelId, promptHash, toolHash, new JsonSerializerOptions()),
        };
    }

    private static GoalStepSnapshot Step(int id, GoalStepState state) => new(
        id,
        $"step-{id}",
        "done",
        state,
        [],
        0,
        false,
        [],
        [],
        false,
        false,
        false);

    private sealed class DynamicRuntime(GoalRunState completionState = GoalRunState.Completed) : IGoalWorkflowRuntime
    {
        public int PlanCalls { get; private set; }
        public int StepCalls { get; private set; }
        public int CompletionCalls { get; private set; }
        public long? BoundFencingToken { get; private set; }

        public Task BindAsync(GoalRun run, long fencingToken, CancellationToken ct)
        {
            BoundFencingToken = fencingToken;
            return Task.CompletedTask;
        }

        public Task<GoalWorkflowState> PlanAsync(GoalWorkflowInput input, CancellationToken ct)
        {
            PlanCalls++;
            return Task.FromResult(new GoalWorkflowState(
                input.GoalRunId,
                [Step(1, GoalStepState.Pending), Step(2, GoalStepState.Pending)],
                [],
                new GoalBudgetSnapshot(0, 0, 0, 0m, DateTimeOffset.UtcNow),
                0,
                false,
                GoalRunState.Executing));
        }

        public Task<GoalWorkflowState> ExecuteNextAsync(GoalWorkflowState state, CancellationToken ct)
        {
            StepCalls++;
            var plan = state.Plan.ToList();
            var step = plan[state.CurrentIndex];
            plan[state.CurrentIndex] = step with { State = GoalStepState.Completed };
            if (step.Id == 1)
                plan.Insert(1, Step(3, GoalStepState.Pending));
            var evidence = new GoalStepExecutionEvidence(
                step.Id,
                GoalStepState.Completed,
                1,
                10,
                5,
                "done",
                "accepted",
                [],
                [],
                [],
                []);
            return Task.FromResult(state with
            {
                Plan = plan,
                Executions = [.. state.Executions, evidence],
                CurrentIndex = state.CurrentIndex + 1,
                Budget = state.Budget with
                {
                    TotalAttempts = state.Budget.TotalAttempts + 1,
                    TotalInputTokens = state.Budget.TotalInputTokens + 10,
                    TotalOutputTokens = state.Budget.TotalOutputTokens + 5,
                },
            });
        }

        public Task<GoalWorkflowOutput> CompleteAsync(GoalWorkflowState state, CancellationToken ct)
        {
            CompletionCalls++;
            return Task.FromResult(new GoalWorkflowOutput(
                state.GoalRunId,
                completionState,
                state.Plan.Count(step => step.State == GoalStepState.Completed),
                state.Plan.Count(step => step.State == GoalStepState.Failed),
                state.FailureSummary));
        }
    }
}
