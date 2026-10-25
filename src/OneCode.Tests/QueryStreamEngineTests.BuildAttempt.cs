// QueryStreamEngineTests partial：持久化 Build 尝试（完成 / 恢复 / 缺计划失败 / 工具暴露）
using System.Collections.Immutable;
using System.Threading.Channels;
using OneCode.App.Query;
using OneCode.App.Services.Agent;
using OneCode.App.Services.BuildMode;
using OneCode.App.Session;
using OneCode.Core.Build;
using OneCode.Core.Tools;
using OneCode.Core.Tasks;
using OneCode.Infrastructure.Build;
using OneCode.Infrastructure.Workflows;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
namespace OneCode.Tests;

public sealed partial class QueryStreamEngineTests
{
    [Fact]
    public async Task StreamWorkflowRunAsync_DurableBuildAttempt_CompletesThroughSharedHost()
    {
        var root = Path.Combine(Path.GetTempPath(), "onecode-chat-durable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var conversation = new Conversation
            {
                Id = SessionId.NewId(),
                WorkingDirectory = root,
            };
            var sessionManager = Substitute.For<ISessionManager>();
            sessionManager.ForegroundConversation.Returns(conversation);
            sessionManager.WorkingDirectory.Returns(root);
            var buildStore = new JsonBuildRunStore(Path.Combine(root, "build-runs"));
            var fingerprint = Substitute.For<IWorkspaceFingerprintProvider>();
            fingerprint.ComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult("fingerprint-durable"));
            var taskService = new TaskService();
            var coordinator = new BuildRunCoordinator(
                buildStore,
                fingerprint,
                new BuildStateTransitionService(),
                new BuildTaskLinker(taskService),
                Substitute.For<ILogger<BuildRunCoordinator>>());
            var registry = new JsonWorkflowRunRegistry(Path.Combine(root, "workflow-runs"));
            var durableHost = new DurableWorkflowHost(
                registry,
                new WorkflowCheckpointStoreFactory(Path.Combine(root, "checkpoints")),
                new WorkflowEventAdapter(),
                NullLogger<DurableWorkflowHost>.Instance);
            var attemptHost = new ControlledBuildAttemptHost(
                durableHost,
                new ControlledBuildAttemptWorkflowCompiler(),
                buildStore,
                registry,
                coordinator);
            var runId = "durable-build-run";
            var runner = Substitute.For<IMainAgentRunner>();
            runner.RunStreamingAsync(
                    Arg.Any<MainAgentRunOptions>(),
                    Arg.Any<ChannelWriter<object>>(),
                    Arg.Any<CancellationToken>())
                .Returns(async callInfo =>
                {
                    var options = callInfo.ArgAt<MainAgentRunOptions>(0);
                    var writer = callInfo.ArgAt<ChannelWriter<object>>(1);
                    // 批准计划的任务必须作为待办预置进 agent run（步骤进度的唯一记账通道）。
                    options.SeedTodoTasks.Should().ContainSingle().Which.Id.Should().Be("t1");
                    await writer.WriteAsync(
                        new AgentResponseUpdate { Contents = { new TextContent("durable complete") } },
                        TestContext.Current.CancellationToken);
                    if (options.BeforeFinalValidation is not null)
                        await options.BeforeFinalValidation(TestContext.Current.CancellationToken);
                    writer.TryComplete();
                    return new MainAgentRunResult(
                        Text: null,
                        TotalInputTokens: 11,
                        TotalOutputTokens: 7,
                        TurnCount: 1,
                        TerminalReason: RunTerminalReason.Completed,
                        FinalValidationStatus: BuildValidationStatus.Passed);
                });
            var capabilities = new ToolCapabilitySet
            {
                AllowedToolNames = new[] { "ReadFile", "Write", "Edit" }.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
                AllowedCategories = ToolCategory.FileWrite,
                MaximumRisk = ToolRisk.Destructive,
                AllowDynamicActivation = true,
                AllowSubAgents = true,
            };
            var capabilityResolver = Substitute.For<IToolCapabilityResolver>();
            capabilityResolver.Resolve(Arg.Any<WorkingMode>()).Returns(capabilities);
            var sut = CreateSutWithDependencies(
                runner,
                sessionManager,
                coordinator,
                attemptHost,
                buildStore,
                capabilityResolver);
            var request = new WorkflowRunRequest(
                runId,
                conversation.Id,
                "Fix Foo.cs line 42 and run FooTests.",
                "system",
                "model1",
                WorkingMode.Build,
                root,
                PrescribedPlan());

            var events = new List<QueryEvent>();
            await foreach (var evt in sut.StreamWorkflowRunAsync(
                request,
                TestContext.Current.CancellationToken))
            {
                events.Add(evt);
            }

            var persisted = await buildStore.LoadAsync(conversation.Id, TestContext.Current.CancellationToken);
            persisted!.State.Should().Be(BuildRunState.Completed, because: persisted.FailureSummary);
            persisted.WorkflowFencingToken.Should().BeGreaterThan(0);
            persisted.Metrics.TurnsCompleted.Should().Be(1);
            persisted.ApprovedToolPolicy.Should().NotBeNull();
            persisted.PlanApprovalSource.Should().Be("plan-mode-approval");
            events.OfType<BuildRunStateEvent>().Select(item => item.State).Should().ContainInOrder(
                BuildRunState.Planned,
                BuildRunState.Implementing,
                BuildRunState.Verifying,
                BuildRunState.Accepting,
                BuildRunState.Completed);
            events.OfType<TextDeltaEvent>().Should().ContainSingle().Which.Text.Should().Be("durable complete");
            events.OfType<BuildRunCompletedEvent>().Should().ContainSingle();
            events.OfType<DoneEvent>().Should().ContainSingle().Which.TerminalReason
                .Should().Be(RunTerminalReason.Completed);
            await runner.Received(1).RunStreamingAsync(
                Arg.Any<MainAgentRunOptions>(),
                Arg.Any<ChannelWriter<object>>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StreamWorkflowRunAsync_ResumedTerminalBuildRun_DoesNotStartAgent()
    {
        var conversation = new Conversation
        {
            Id = SessionId.NewId(),
            WorkingDirectory = Path.GetTempPath(),
        };
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.ForegroundConversation.Returns(conversation);
        sessionManager.WorkingDirectory.Returns(conversation.WorkingDirectory);
        var coordinator = Substitute.For<IBuildRunCoordinator>();
        var now = DateTimeOffset.UtcNow;
        var completed = new BuildRun
        {
            Id = BuildRunId.New(),
            ConversationId = conversation.Id,
            State = BuildRunState.Completed,
            TerminalReason = RunTerminalReason.Completed,
            CreatedAt = now,
            UpdatedAt = now,
        };
        coordinator.BeginOrResumeAsync(
                conversation.Id,
                Arg.Any<string>(),
                conversation.WorkingDirectory,
                Arg.Any<CancellationToken>(),
                Arg.Any<Action<BuildRun>?>(),
                Arg.Any<BuildPlan?>())
            .Returns(completed);
        var runner = Substitute.For<IMainAgentRunner>();
        var sut = CreateSutWithDependencies(runner, sessionManager, coordinator);
        var request = new WorkflowRunRequest(
            "build-recovery",
            conversation.Id,
            "resume",
            "sys",
            "model1",
            WorkingMode.Build,
            conversation.WorkingDirectory,
            PrescribedPlan());

        var events = new List<QueryEvent>();
        await foreach (var evt in sut.StreamWorkflowRunAsync(
            request,
            TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        events.OfType<BuildRunCompletedEvent>().Should().ContainSingle();
        events.OfType<DoneEvent>().Should().ContainSingle()
            .Which.TerminalReason.Should().Be(RunTerminalReason.Completed);
        await runner.DidNotReceive().RunStreamingAsync(
            Arg.Any<MainAgentRunOptions>(),
            Arg.Any<ChannelWriter<object>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamWorkflowRunAsync_MissingPrescribedPlan_FailsWithoutStartingAgent()
    {
        // 受控执行恒由上层批准的预置计划驱动（WorkflowRunRequest.PrescribedBuildPlan
        // 必填不变量）——缺失即拒绝，不启动 agent。
        var conversation = new Conversation
        {
            Id = SessionId.NewId(),
            WorkingDirectory = Path.GetTempPath(),
        };
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.ForegroundConversation.Returns(conversation);
        sessionManager.WorkingDirectory.Returns(conversation.WorkingDirectory);
        var runner = Substitute.For<IMainAgentRunner>();
        var sut = CreateSutWithDependencies(runner, sessionManager, Substitute.For<IBuildRunCoordinator>());
        var request = new WorkflowRunRequest(
            "missing-prescribed-plan",
            conversation.Id,
            "execute",
            "system",
            "model",
            WorkingMode.Build,
            Path.GetTempPath(),
            null!);

        var act = async () =>
        {
            await foreach (var _ in sut.StreamWorkflowRunAsync(request, TestContext.Current.CancellationToken))
            {
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*prescribed plan*");
        await runner.DidNotReceive().RunStreamingAsync(
            Arg.Any<MainAgentRunOptions>(),
            Arg.Any<ChannelWriter<object>>(),
            Arg.Any<CancellationToken>());
    }

}








