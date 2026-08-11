using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services;
using OneCode.App.Services.Coordinator;
using OneCode.App.Tools;
using OneCode.Core.Models;
using OneCode.Core.Prompt;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Workflows;

namespace OneCode.Tests;

public sealed class MafTeamOrchestrationTests
{
    private static TeamOrchestrationService CreateSut()
    {
        var chatClient = Substitute.For<IChatClient>();
        var promptManager = new PromptManager();
        promptManager.RegisterTemplate(new PromptTemplate(
            PromptComposer.HarnessPromptName,
            "# Prompt injection defense — MANDATORY\nShared harness."));
        var agentFactory = new TeamAgentFactory(
            chatClient,
            NullLoggerFactory.Instance,
            NullLogger<TeamAgentFactory>.Instance,
            Substitute.For<IServiceProvider>(),
            Substitute.For<IModelManager>(),
            promptManager,
            new PromptComposer(promptManager),
            new TeamAgentToolSources(
                Substitute.For<ICacheSafeParamsProvider>(),
                new ToolCatalog(new Lazy<List<AIFunction>>(() => []), new ToolMetadataRegistry(), null)),
            new TeamAgentPipelineDependencies(null!, null!, null!, Substitute.For<OneCode.App.Session.ISessionConversationAccess>()));
        var workflowRunner = new TeamWorkflowRunner(
            agentFactory,
            NullLogger<TeamWorkflowRunner>.Instance,
            executionEnvironment: null);
        var teamRunStore = Substitute.For<OneCode.Core.Coordinator.ITeamRunStore>();
        OneCode.Core.Coordinator.TeamRun? savedTeamRun = null;
        teamRunStore.TrySaveAsync(
                Arg.Any<OneCode.Core.Coordinator.TeamRun>(),
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                savedTeamRun = call.Arg<OneCode.Core.Coordinator.TeamRun>();
                return true;
            });
        teamRunStore.LoadAsync(
                Arg.Any<OneCode.Core.Coordinator.TeamRunId>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => savedTeamRun);
        var stateMachine = new TeamRunStateMachine();
        var qualityGateRunner = new TeamQualityGateRunner([]);
        var deliveryReportBuilder = new DeliveryReportBuilder();
        var teamRunService = new TeamRunApplicationService(
            teamRunStore, stateMachine, qualityGateRunner, deliveryReportBuilder);
        var requirementService = new TeamRequirementService(
            new OneCode.App.Services.BuildMode.RequirementAssessmentService());
        var clarification = Substitute.For<IClarificationInteractionService>();
        // By default, answer clarification questions with a positive response to proceed.
        clarification.AskAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new ClarificationInteractionResult("yes", IsCancelled: false));
        var workingDir = Substitute.For<IWorkingDirectoryAccessor>();
        workingDir.WorkingDirectory.Returns(Environment.CurrentDirectory);
        var probeRoot = Path.Combine(Path.GetTempPath(), "onecode-maf-team-tests", Guid.NewGuid().ToString("N"));
        var registry = new JsonWorkflowRunRegistry(Path.Combine(probeRoot, "runs"));
        var checkpointFactory = new OneCode.App.Services.Agent.WorkflowCheckpointStoreFactory(
            Path.Combine(probeRoot, "checkpoints"));
        var eventAdapter = new OneCode.App.Services.Agent.WorkflowEventAdapter();
        var durableHost = new OneCode.App.Services.Agent.DurableWorkflowHost(
            registry, checkpointFactory, eventAdapter, NullLogger<OneCode.App.Services.Agent.DurableWorkflowHost>.Instance);
        var taskCompiler = new TeamTaskWorkflowCompiler();
        var taskWorkflowHost = new TeamTaskWorkflowHost(durableHost, taskCompiler, teamRunStore, registry);
        var approvalCompiler = new TeamApprovalWorkflowCompiler();
        var approvalHost = new TeamApprovalWorkflowHost(durableHost, approvalCompiler, registry);
        var clarificationCompiler = new TeamClarificationWorkflowCompiler();
        var clarificationHost = new TeamClarificationWorkflowHost(durableHost, clarificationCompiler, registry);
        return new TeamOrchestrationService(
            workflowRunner,
            NullLoggerFactory.Instance,
            NullLogger<TeamOrchestrationService>.Instance,
            teamRunService,
            requirementService,
            clarification,
            workingDir,
            taskWorkflowHost,
            approvalHost,
            clarificationHost,
            teamRunStore);
    }

    [Fact]
    public async Task RegisterTeamAsync_YamlConfig_RegistersTeam()
    {
        var tempDir = CreateTempDir();
        try
        {
            var teamDir = Path.Combine(tempDir, "my-team");
            Directory.CreateDirectory(teamDir);
            var yaml = """
                name: my-team
                description: Test team
                template: groupchat
                workers:
                  - name: researcher
                    role: researcher
                    instructions: Research the codebase
                  - name: reviewer
                    role: reviewer
                    instructions: Review code changes
                max_rounds: 10
                """;
            var yamlPath = Path.Combine(teamDir, "team.yaml");
            await File.WriteAllTextAsync(yamlPath, yaml, TestContext.Current.CancellationToken);

            var sut = CreateSut();

            await sut.RegisterTeamAsync("my-team", yamlPath, TestContext.Current.CancellationToken);

            sut.RegisteredTeams.Should().Contain("my-team");
            sut.GetTeamMode("my-team").Should().Be("groupchat");
        }
        finally
        {
            SafeDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task RegisterTeamAsync_MagenticTemplate_DetectsMode()
    {
        var tempDir = CreateTempDir();
        try
        {
            var teamDir = Path.Combine(tempDir, "magentic-team");
            Directory.CreateDirectory(teamDir);
            var yaml = """
                name: magentic-team
                description: Magentic style team
                template: magentic-orchestrator
                workers:
                  - name: coder
                    role: executor
                    instructions: Write code
                max_rounds: 5
                """;
            var yamlPath = Path.Combine(teamDir, "team.yaml");
            await File.WriteAllTextAsync(yamlPath, yaml, TestContext.Current.CancellationToken);

            var sut = CreateSut();

            await sut.RegisterTeamAsync("magentic-team", yamlPath, TestContext.Current.CancellationToken);

            sut.GetTeamMode("magentic-team").Should().Be("magentic");
        }
        finally
        {
            SafeDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task UnregisterTeamAsync_RemovesFromRegistry()
    {
        var tempDir = CreateTempDir();
        try
        {
            var teamDir = Path.Combine(tempDir, "temp-team");
            Directory.CreateDirectory(teamDir);
            var yaml = """
                name: temp-team
                description: Temporary
                template: groupchat
                workers:
                  - name: helper
                    role: general
                """;
            var yamlPath = Path.Combine(teamDir, "team.yaml");
            await File.WriteAllTextAsync(yamlPath, yaml, TestContext.Current.CancellationToken);

            var sut = CreateSut();

            await sut.RegisterTeamAsync("temp-team", yamlPath, TestContext.Current.CancellationToken);
            sut.RegisteredTeams.Should().Contain("temp-team");

            await sut.UnregisterTeamAsync("temp-team", TestContext.Current.CancellationToken);
            sut.RegisteredTeams.Should().NotContain("temp-team");
        }
        finally
        {
            SafeDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task RunTeamStreamingAsync_UnregisteredTeam_ReturnsError()
    {
        var sut = CreateSut();

        var result = await sut.RunTeamStreamingAsync("nonexistent", "Do something", eventSink: null, TestContext.Current.CancellationToken);

        result.Output.Should().Contain("not found");
        result.TurnsCompleted.Should().Be(0);
    }

    [Fact]
    public void GetTeamMode_UnknownTeam_ReturnsNull()
    {
        var sut = CreateSut();

        sut.GetTeamMode("unknown").Should().BeNull();
    }

    [Fact]
    public void TuiEventMapper_FileChanged_MapsExactlyOnceToFileChange()
    {
        var evt = new OneCode.Core.Coordinator.OrchestrationEvent.FileChanged(
            "executor",
            "Foo.cs",
            ["new"],
            ["old"]);

        var mapped = OneCode.App.Tui.TuiEventMapper.MapOrchestrationEventToTuiEvent(evt);

        mapped.Should().BeOfType<OneCode.App.Tui.TuiFileChange>()
            .Which.FileName.Should().Be("Foo.cs");
    }

    [Fact]
    public void TuiEventMapper_TeamPlanApproval_MapsToNotificationEvent()
    {
        var approval = new OneCode.Core.Coordinator.OrchestrationEvent.TeamPlanApprovalRequest(
            OneCode.Core.Coordinator.TeamRunId.NewId(),
            "feature-team",
            "Implement feature",
            ["Code"],
            ["Build"]);

        var mapped = OneCode.App.Tui.TuiEventMapper.MapOrchestrationEventToTuiEvent(approval);

        mapped.Should().BeOfType<OneCode.App.Tui.TuiTeamPlanApproval>()
            .Which.TeamName.Should().Be("feature-team");
    }

    [Fact]
    public async Task RunTeamStreamingAsync_UnregisteredTeam_EmitsErrorEvent()
    {
        var sut = CreateSut();

        var events = new List<OneCode.Core.Coordinator.OrchestrationEvent>();
        var result = await sut.RunTeamStreamingAsync(
            "nonexistent", "Do something",
            evt => events.Add(evt),
            TestContext.Current.CancellationToken);

        result.Output.Should().Contain("not found");
        result.TurnsCompleted.Should().Be(0);
        events.Should().Contain(e => e is OneCode.Core.Coordinator.OrchestrationEvent.Error);
    }

    [Fact]
    public async Task RunTeamStreamingAsync_RegisteredTeam_EmitsAgentCoordinationEvent()
    {
        var tempDir = CreateTempDir();
        try
        {
            var teamDir = Path.Combine(tempDir, "stream-team");
            Directory.CreateDirectory(teamDir);
            var yaml = """
                name: stream-team
                description: Test streaming
                template: groupchat
                workers:
                  - name: researcher
                    role: researcher
                    instructions: Research the codebase
                  - name: planner
                    role: planner
                    instructions: Plan the work
                max_rounds: 5
                """;
            var yamlPath = Path.Combine(teamDir, "team.yaml");
            await File.WriteAllTextAsync(yamlPath, yaml, TestContext.Current.CancellationToken);

            var sut = CreateSut();

            await sut.RegisterTeamAsync("stream-team", yamlPath, TestContext.Current.CancellationToken);

            var events = new List<OneCode.Core.Coordinator.OrchestrationEvent>();
            await sut.RunTeamStreamingAsync(
                "stream-team", "Analyze the codebase",
                evt => events.Add(evt),
                TestContext.Current.CancellationToken);

            // 验证至少发射了 AgentCoordination 事件（user → lead/researcher）
            events.Should().Contain(e => e is OneCode.Core.Coordinator.OrchestrationEvent.AgentCoordination);
        }
        finally
        {
            SafeDeleteDir(tempDir);
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"MafTeamTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void SafeDeleteDir(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
