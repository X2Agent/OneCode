using System.Text.Json;
using OneCode.App.Services.Agent;
using OneCode.App.Services.BuildMode;
using OneCode.Core.Build;
using OneCode.Core.Domain;
using OneCode.Core.Workflows;
using OneCode.Core.Tasks;
using OneCode.Infrastructure.Build;
using OneCode.Infrastructure.Workflows;
using NSubstitute;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OneCode.Tests;

public sealed class ControlledBuildAttemptWorkflowTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "onecode-tests", "controlled-build-attempt", Guid.NewGuid().ToString("N"));

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
    public void Compile_TaskInputOrderDoesNotChangeDefinitionHash()
    {
        var compiler = new ControlledBuildAttemptWorkflowCompiler();
        var first = CreateBuildRun([
            CreateTask("b", ["a"]),
            CreateTask("a", []),
        ]);
        var second = first with
        {
            Plan = first.Plan! with { Tasks = [CreateTask("a", []), CreateTask("b", ["a"])] },
        };

        var firstDefinition = compiler.Compile(first, 1, "model", "system", "tools", new StubRuntime());
        var secondDefinition = compiler.Compile(second, 1, "model", "system", "tools", new StubRuntime());

        firstDefinition.Registration.DefinitionHash.Should().Be(secondDefinition.Registration.DefinitionHash);
        firstDefinition.Registration.RunId.Should().Be(secondDefinition.Registration.RunId);
    }

    [Fact]
    public void Compile_DefinitionInputsChangeHashAndAttemptIdentity()
    {
        var compiler = new ControlledBuildAttemptWorkflowCompiler();
        var run = CreateBuildRun([CreateTask("implementation", [])]);
        var baseline = compiler.Compile(run, 1, "model-a", "system-a", "tools-a", new StubRuntime());

        compiler.Compile(run, 1, "model-b", "system-a", "tools-a", new StubRuntime())
            .Registration.DefinitionHash.Should().NotBe(baseline.Registration.DefinitionHash);
        compiler.Compile(run, 1, "model-a", "system-b", "tools-a", new StubRuntime())
            .Registration.DefinitionHash.Should().NotBe(baseline.Registration.DefinitionHash);
        compiler.Compile(run, 1, "model-a", "system-a", "tools-b", new StubRuntime())
            .Registration.DefinitionHash.Should().NotBe(baseline.Registration.DefinitionHash);

        var nextAttempt = compiler.Compile(run, 2, "model-a", "system-a", "tools-a", new StubRuntime());
        nextAttempt.Registration.RunId.Should().Be(baseline.Registration.RunId);
        nextAttempt.Input.OperationId.Should().NotBe(baseline.Input.OperationId);
        nextAttempt.Registration.DefinitionHash.Should().Be(baseline.Registration.DefinitionHash);
    }

    [Fact]
    public void Compile_DefinitionHash_IsSensitiveToSerializerOptions()
    {
        var compiler = new ControlledBuildAttemptWorkflowCompiler();
        var run = CreateBuildRun([CreateTask("implementation", [])]);

        var defaultDefinition = compiler.Compile(run, 1, "model-a", "system-a", "tools-a", new StubRuntime());
        // null 与显式 default options 是不同契约表达 → hash 不同；同配置则稳定。
        compiler.Compile(
                run, 1, "model-a", "system-a", "tools-a", new StubRuntime(), new JsonSerializerOptions())
            .Registration.DefinitionHash.Should().NotBe(defaultDefinition.Registration.DefinitionHash);
        compiler.Compile(
                run, 1, "model-a", "system-a", "tools-a", new StubRuntime(),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            .Registration.DefinitionHash.Should().NotBe(defaultDefinition.Registration.DefinitionHash);
        compiler.Compile(
                run, 1, "model-a", "system-a", "tools-a", new StubRuntime(), new JsonSerializerOptions())
            .Registration.DefinitionHash.Should().Be(compiler.Compile(
                run, 1, "model-a", "system-a", "tools-a", new StubRuntime(), new JsonSerializerOptions())
                .Registration.DefinitionHash);
    }

    [Fact]
    public async Task Host_ExecutesTypedAttemptAndCompletesDurableRun()
    {
        var registry = new JsonWorkflowRunRegistry(Path.Combine(_root, "registry"));
        var durableHost = new DurableWorkflowHost(
            registry,
            new WorkflowCheckpointStoreFactory(Path.Combine(_root, "checkpoints")),
            new WorkflowEventAdapter(),
            NullLogger<DurableWorkflowHost>.Instance);
        var buildStore = new JsonBuildRunStore(Path.Combine(_root, "build-runs"));
        var coordinator = Substitute.For<IBuildRunCoordinator>();
        coordinator.PrepareAttemptAsync(
                Arg.Any<BuildRunId>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(call => buildStore.LoadByIdAsync(
                call.ArgAt<BuildRunId>(0),
                call.ArgAt<CancellationToken>(2))!);
        var host = new ControlledBuildAttemptHost(
            durableHost,
            new ControlledBuildAttemptWorkflowCompiler(),
            buildStore,
            registry,
            coordinator);
        var runtime = new StubRuntime();
        var run = CreateBuildRun([CreateTask("implementation", [])]);
        await buildStore.SaveAsync(run, 0, TestContext.Current.CancellationToken);
        run = (await buildStore.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken))!;

        var result = await host.RunAsync(
            run,
            1,
            "model",
            "system",
            "tools",
            runtime,
            new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);

        runtime.Input.Should().NotBeNull();
        runtime.Input!.BuildRunId.Should().Be(run.Id);
        runtime.Input.Attempt.Should().Be(1);
        runtime.Input.OperationId.Should().Contain(run.Id.ToString());
        result.Run.State.Should().Be(WorkflowRunState.Completed);
        result.Run.CheckpointId.Should().NotBeNullOrWhiteSpace();
        result.Events.Should().Contain(item => item is WorkflowRuntimeEvent.Output);
        var claimedBuildRun = await buildStore.LoadByIdAsync(run.Id, TestContext.Current.CancellationToken);
        claimedBuildRun!.WorkflowFencingToken.Should().Be(result.Run.FencingToken);
    }

    [Fact]
    public async Task Host_TakeoverFromVerifying_StartsNewGenerationAndFencesOldAttempt()
    {
        var registry = new JsonWorkflowRunRegistry(Path.Combine(_root, "takeover-registry"));
        var buildStore = new JsonBuildRunStore(Path.Combine(_root, "takeover-build-runs"));
        var checkpointFactory = new WorkflowCheckpointStoreFactory(Path.Combine(_root, "takeover-checkpoints"));
        var durableHost = new DurableWorkflowHost(registry, checkpointFactory, new WorkflowEventAdapter(),
            NullLogger<DurableWorkflowHost>.Instance);
        var fingerprint = Substitute.For<IWorkspaceFingerprintProvider>();
        fingerprint.ComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("fingerprint-v1"));
        var coordinator = new BuildRunCoordinator(
            buildStore,
            fingerprint,
            new RequirementAssessmentService(),
            new BuildStateTransitionService(),
            new TaskService(),
            Substitute.For<ILogger<BuildRunCoordinator>>());
        var host = new ControlledBuildAttemptHost(
            durableHost,
            new ControlledBuildAttemptWorkflowCompiler(),
            buildStore,
            registry,
            coordinator);
        var runtime = new StubRuntime();
        var buildRun = CreateBuildRun([CreateTask("implementation", [])]) with
        {
            State = BuildRunState.Verifying,
        };
        await buildStore.SaveAsync(buildRun, 0, TestContext.Current.CancellationToken);
        buildRun = (await buildStore.LoadByIdAsync(buildRun.Id, TestContext.Current.CancellationToken))!;
        var definition = new ControlledBuildAttemptWorkflowCompiler().Compile(
            buildRun,
            1,
            "model",
            "system",
            "tools",
            runtime,
            new JsonSerializerOptions());
        await using (var firstLease = await registry.TryAcquireAsync(
            definition.Registration,
            TestContext.Current.CancellationToken))
        {
            firstLease.Should().NotBeNull();
            var first = await registry.BeginGenerationAsync(
                definition.Registration.RunId,
                firstLease!.FencingToken,
                1,
                TestContext.Current.CancellationToken);
            await registry.ReconcileCheckpointAsync(
                first.RunId,
                first.FencingToken,
                "old-checkpoint",
                TestContext.Current.CancellationToken);
            await registry.RegisterPendingRequestAsync(
                first.RunId,
                first.FencingToken,
                new WorkflowPendingRequest(
                    "old-request",
                    "old-port",
                    "old-command",
                    DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
            buildRun = await buildStore.ClaimWorkflowAsync(
                buildRun.Id,
                first.FencingToken,
                buildRun.Version,
                TestContext.Current.CancellationToken);
        }

        var result = await host.RunAsync(
            buildRun,
            2,
            "model",
            "system",
            "tools",
            runtime,
            new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);

        result.Run.ExecutionGeneration.Should().Be(2);
        result.Run.FencingToken.Should().BeGreaterThan(1);
        result.Run.PendingRequest.Should().BeNull();
        result.Run.CheckpointId.Should().NotBe("old-checkpoint");
        var persisted = await buildStore.LoadByIdAsync(buildRun.Id, TestContext.Current.CancellationToken);
        persisted!.State.Should().Be(BuildRunState.Implementing);
        persisted.WorkflowFencingToken.Should().Be(result.Run.FencingToken);
        var staleSave = () => buildStore.SaveFencedAsync(
            persisted with { WorkflowFencingToken = 1 },
            persisted.Version,
            1,
            TestContext.Current.CancellationToken);
        await staleSave.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Stale*");
    }

    private static BuildRun CreateBuildRun(IReadOnlyList<BuildPlanTask> tasks)
    {
        var now = DateTimeOffset.UtcNow;
        return new BuildRun
        {
            Id = new BuildRunId("br-0123456789abcdef0123456789abcdef"),
            ConversationId = SessionId.NewId(),
            State = BuildRunState.Implementing,
            IntakePrompt = "implement",
            WorkingDirectory = Path.GetTempPath(),
            WorkspaceFingerprint = "fingerprint-v1",
            Scope = new BuildScopeSnapshot(
                "Implement scope",
                ["src"],
                ["docs"],
                ["preserve behavior"],
                [new AcceptanceCriterion("accept-1", "Tests pass", true)],
                "user",
                now),
            Plan = new BuildPlan(
                "Implement plan",
                tasks,
                ["dotnet test"],
                ["regression"],
                ["unrelated refactor"],
                RequireExplicitTaskCompletion: true),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static BuildPlanTask CreateTask(string id, IReadOnlyList<string> dependencies)
        => new(
            id,
            $"Title {id}",
            $"Description {id}",
            dependencies,
            [$"src/{id}.cs"],
            ["accept-1"]);

    private sealed class StubRuntime : IControlledBuildAttemptRuntime
    {
        public ControlledBuildAttemptInput? Input { get; private set; }

        public Task<MainAgentRunResult> ExecuteAsync(
            ControlledBuildAttemptInput input,
            CancellationToken ct = default)
        {
            Input = input;
            return Task.FromResult(new MainAgentRunResult(
                "done",
                TotalInputTokens: 10,
                TotalOutputTokens: 5,
                TurnCount: 1,
                TerminalReason: BuildTerminalReason.Completed,
                FinalValidationStatus: BuildValidationStatus.Passed));
        }
    }
}
