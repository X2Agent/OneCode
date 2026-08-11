using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Services.Agent;
using OneCode.App.Services.Coordinator;
using OneCode.Core.Coordinator;
using OneCode.Infrastructure.Workflows;

namespace OneCode.Tests;

public sealed class TeamApprovalWorkflowTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "onecode-tests", "team-approval", Guid.NewGuid().ToString("N"));

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
    public void Compile_PlanInputOrderDoesNotChangeApprovalDefinitionHash()
    {
        var compiler = new TeamApprovalWorkflowCompiler();
        var config = CreateConfig();
        var first = compiler.Compile(
            "team-a", TeamRunId.NewId(), config, "model",
            new TeamPlanApprovalInput("run-1", "team-a", "Implement feature", ["b-tasks", "a-tasks"], ["build", "acceptance"]));
        var second = compiler.Compile(
            "team-a", TeamRunId.NewId(), config, "model",
            new TeamPlanApprovalInput("run-2", "team-a", "Implement feature", ["a-tasks", "b-tasks"], ["acceptance", "build"]));

        first.Registration.DefinitionHash.Should().Be(second.Registration.DefinitionHash);
        // RunId 内嵌 run.Id，故不同的 Run 实例拥有不同 RunId。
        first.Registration.RunId.Should().StartWith("team/");
    }

    [Fact]
    public async Task RespondApproves_SameGeneration_ResumesToGranted()
    {
        var host = CreateHost(out _);
        var runId = TeamRunId.NewId();
        var input = new TeamPlanApprovalInput(runId.ToString(), "team-a", "Implement feature", ["a"], ["build"]);
        var config = CreateConfig();

        var first = await host.RunApprovalAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);
        first.Durable.IsPending.Should().BeTrue();
        var pending = first.PendingRequest!;

        var response = new ExternalResponse(
            new RequestPortInfo(
                new TypeId(typeof(TeamPlanApprovalInput)),
                new TypeId(typeof(TeamPlanApprovalDecision)),
                pending.PortId),
            pending.RequestId,
            new PortableValue(new TeamPlanApprovalDecision(true)));

        var second = await host.RunApprovalAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            externalResponse: response,
            ct: TestContext.Current.CancellationToken);

        second.Durable.IsPending.Should().BeFalse();
        second.ApprovalGranted.Should().BeTrue();
    }

    [Fact]
    public async Task RespondDenies_SameGeneration_ResumesToDenied()
    {
        var host = CreateHost(out _);
        var runId = TeamRunId.NewId();
        var input = new TeamPlanApprovalInput(runId.ToString(), "team-a", "Implement feature", ["a"], ["build"]);
        var config = CreateConfig();

        var first = await host.RunApprovalAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);
        var pending = first.PendingRequest!;

        var response = new ExternalResponse(
            new RequestPortInfo(
                new TypeId(typeof(TeamPlanApprovalInput)),
                new TypeId(typeof(TeamPlanApprovalDecision)),
                pending.PortId),
            pending.RequestId,
            new PortableValue(new TeamPlanApprovalDecision(false)));

        var second = await host.RunApprovalAsync(
            "team-a", runId, config, "model", input, new JsonSerializerOptions(),
            externalResponse: response,
            ct: TestContext.Current.CancellationToken);

        second.Durable.IsPending.Should().BeFalse();
        second.ApprovalGranted.Should().BeFalse();
    }

    [Fact]
    public void Compile_PlanContentChangesApprovalDefinitionHash()
    {
        var compiler = new TeamApprovalWorkflowCompiler();
        var config = CreateConfig();
        var baseline = compiler.Compile(
            "team-a", TeamRunId.NewId(), config, "model",
            new TeamPlanApprovalInput("run-1", "team-a", "Implement feature", ["a"], ["build"]));

        compiler.Compile(
                "team-a", TeamRunId.NewId(), config, "model",
                new TeamPlanApprovalInput("run-2", "team-a", "Implement ANOTHER feature", ["a"], ["build"]))
            .Registration.DefinitionHash.Should().NotBe(baseline.Registration.DefinitionHash);
        compiler.Compile(
                "team-a", TeamRunId.NewId(), config, "model",
                new TeamPlanApprovalInput("run-3", "team-a", "Implement feature", ["a", "b"], ["build"]))
            .Registration.DefinitionHash.Should().NotBe(baseline.Registration.DefinitionHash);
    }

    [Fact]
    public async Task RunApproval_SuspendsWithoutTerminalDecision()
    {
        var host = CreateHost(out _);
        var runId = TeamRunId.NewId();
        var input = new TeamPlanApprovalInput(
            runId.ToString(), "team-a", "Implement feature", ["implement", "review"], ["build"]);

        var result = await host.RunApprovalAsync(
            "team-a", runId, CreateConfig(), "model", input, new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);

        result.Durable.IsPending.Should().BeTrue();
        result.PendingRequest.Should().NotBeNull();
        result.PendingRequest!.CommandId.Should().Be($"team/{runId}/approve");
        result.PendingRequest.PortId.Should().Be("team-plan-approval-v1");
        result.ApprovalGranted.Should().BeNull();
    }

    [Fact]
    public async Task RunApproval_SecondInvocationWithoutResponse_StaysPending()
    {
        var host = CreateHost(out _);
        var runId = TeamRunId.NewId();
        var input = new TeamPlanApprovalInput(runId.ToString(), "team-a", "Implement feature", ["a"], ["build"]);

        var first = await host.RunApprovalAsync(
            "team-a", runId, CreateConfig(), "model", input, new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);
        first.Durable.IsPending.Should().BeTrue();

        // 未响应时再次续跑：仍挂在同一 PendingRequest 上（不产生终态）。
        var again = await host.RunApprovalAsync(
            "team-a", runId, CreateConfig(), "model", input, new JsonSerializerOptions(),
            ct: TestContext.Current.CancellationToken);
        again.Durable.IsPending.Should().BeTrue();
        again.PendingRequest.Should().NotBeNull();
        again.ApprovalGranted.Should().BeNull();
    }

    private TeamApprovalWorkflowHost CreateHost(out JsonWorkflowRunRegistry registry)
    {
        registry = new JsonWorkflowRunRegistry(Path.Combine(_root, "registry"));
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