using System.Collections.Immutable;
using System.Threading.Channels;
using OneCode.App.Query;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Services.BuildMode;
using OneCode.App.Services.Compact;
using OneCode.App.Services.Notifier;
using OneCode.App.Services.Observability;
using OneCode.App.Services.PlanMode;
using OneCode.App.Session;
using OneCode.App.Tools;
using OneCode.App.Tui;
using OneCode.Core.Build;
using OneCode.Core.Domain;
using OneCode.Core.Hooks;
using OneCode.Core.Models;
using OneCode.Core.Permissions;
using OneCode.Core.Prompt;
using OneCode.Core.Tools;
using OneCode.Core.Tasks;
using OneCode.Infrastructure.Api;
using OneCode.Infrastructure.Build;
using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Workflows;
using OneCode.Tests.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace OneCode.Tests;

public sealed class ChatServiceTests
{
    [Fact]
    public async Task StreamQueryAsync_PreCancelledToken_StopsWithoutStreaming()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var events = new List<QueryEvent>();
        var threwOperationCanceled = false;
        try
        {
            await foreach (var evt in sut.StreamQueryAsync("test", "sys", "model1", ct: cts.Token))
                events.Add(evt);
        }
        catch (OperationCanceledException)
        {
            threwOperationCanceled = true;
        }

        // Must explicitly assert the exception was thrown — otherwise the test
        // passes even if cancellation logic is broken (empty events either way).
        threwOperationCanceled.Should().BeTrue("pre-cancelled token should trigger OperationCanceledException");
        events.OfType<TextDeltaEvent>().Should().BeEmpty("no text should be emitted when cancelled upfront");
    }

    [Fact]
    public void NormalizeForToolCallingTransport_StripsEmptyText_WhenToolContentsExist()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.Assistant, new AIContent[]
            {
                new TextContent(string.Empty),
                new FunctionCallContent("call_1", "LS", null)
            }),
            new ChatMessage(ChatRole.User, new AIContent[]
            {
                new TextContent(string.Empty),
                new FunctionResultContent("call_1", "ok")
            })
        };

        var normalized = MessageApiInvariantHelper.NormalizeForToolCallingTransport(messages);

        foreach (var message in normalized)
        {
            message.Contents.Should().NotContain(
                c => c is TextContent && ((TextContent)c).Text == string.Empty);
        }
        normalized[0].Contents.Should().Contain(c => c is FunctionCallContent);
        normalized[1].Contents.Should().Contain(c => c is FunctionResultContent);
    }

    // Core streaming logic tests — verify tool dedup, usage extraction, turn boundary

    [Fact]
    public async Task StreamQueryAsync_DeduplicatesToolCallEvents_WithSameCallId()
    {
        var callId = "call_dedup_1";
        var fcc = new FunctionCallContent(callId, "Read", new Dictionary<string, object?> { ["path"] = "test.txt" });
        var frc = new FunctionResultContent(callId, "ok");

        var (sut, _) = CreateSutWithMockedRunner(writer =>
        {
            // First update: tool call + tool result
            writer.TryWrite(new AgentResponseUpdate { Contents = { fcc, frc } });
            // Second update: MAF replays the same call+result across turn boundary
            writer.TryWrite(new AgentResponseUpdate { Contents = { fcc, frc } });
            // Text after tool results to trigger turn boundary
            writer.TryWrite(new AgentResponseUpdate { Contents = { new TextContent("done") } });
        });

        var events = await CollectEventsAsync(sut);

        // ToolStart should appear exactly once despite duplicate replay
        var toolStarts = events.OfType<ToolStartEvent>().ToList();
        toolStarts.Should().HaveCount(1, "duplicate tool call with same CallId must be deduplicated");
        toolStarts[0].ToolName.Should().Be("Read");

        // ToolDone should appear exactly once
        var toolDones = events.OfType<ToolDoneEvent>().ToList();
        toolDones.Should().HaveCount(1, "duplicate tool result with same CallId must be deduplicated");
        toolDones[0].IsError.Should().BeFalse();
    }

    [Fact]
    public async Task StreamWorkflowRunAsync_CancelledAfterCompletedToolBatch_PersistsSealedBatch()
    {
        var sessionId = SessionId.NewId();
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(Path.GetTempPath());
        var runner = Substitute.For<IMainAgentRunner>();
        runner.RunStreamingAsync(
                Arg.Any<MainAgentRunOptions>(),
                Arg.Do<ChannelWriter<object>>(writer =>
                {
                    writer.TryWrite(new AgentResponseUpdate
                    {
                        Contents = { new FunctionCallContent("call_cancelled_complete", "Read", null) }
                    });
                    writer.TryWrite(new AgentResponseUpdate
                    {
                        Contents = { new FunctionResultContent("call_cancelled_complete", "ok") }
                    });
                }),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => WaitForCancellationAsync(callInfo.ArgAt<CancellationToken>(2)));
        var sut = CreateSutWithDependencies(
            runner,
            sessionManager,
            Substitute.For<IBuildRunCoordinator>());
        var request = new WorkflowRunRequest(
            "plan-run-cancelled-complete",
            sessionId,
            "execute",
            "system",
            "model",
            WorkingMode.Plan,
            Path.GetTempPath());
        using var cts = new CancellationTokenSource();

        var act = async () =>
        {
            await foreach (var evt in sut.StreamWorkflowRunAsync(request, cts.Token))
            {
                if (evt is ToolDoneEvent)
                    cts.Cancel();
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        await sessionManager.Received(1).AppendCompletedToolBatchesAsync(
            sessionId,
            Arg.Is<IReadOnlyList<CompletedToolBatch>>(batches =>
                batches.Count == 1
                && batches[0].IsComplete
                && batches[0].Calls.Single().CallId == "call_cancelled_complete"
                && batches[0].Results.Single().CallId == "call_cancelled_complete"),
            CancellationToken.None);
    }

    [Fact]
    public async Task StreamWorkflowRunAsync_CancelledWithOpenToolBatch_DoesNotPersistOrphanedCall()
    {
        var sessionId = SessionId.NewId();
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(Path.GetTempPath());
        var runner = Substitute.For<IMainAgentRunner>();
        runner.RunStreamingAsync(
                Arg.Any<MainAgentRunOptions>(),
                Arg.Do<ChannelWriter<object>>(writer => writer.TryWrite(new AgentResponseUpdate
                {
                    Contents = { new FunctionCallContent("call_cancelled_open", "Read", null) }
                })),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => WaitForCancellationAsync(callInfo.ArgAt<CancellationToken>(2)));
        var sut = CreateSutWithDependencies(
            runner,
            sessionManager,
            Substitute.For<IBuildRunCoordinator>());
        var request = new WorkflowRunRequest(
            "plan-run-cancelled-open",
            sessionId,
            "execute",
            "system",
            "model",
            WorkingMode.Plan,
            Path.GetTempPath());
        using var cts = new CancellationTokenSource();

        var act = async () =>
        {
            await foreach (var evt in sut.StreamWorkflowRunAsync(request, cts.Token))
            {
                if (evt is ToolStartEvent)
                    cts.Cancel();
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        await sessionManager.DidNotReceive().AppendCompletedToolBatchesAsync(
            Arg.Any<SessionId>(),
            Arg.Any<IReadOnlyList<CompletedToolBatch>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamQueryAsync_ExtractsUsageFromUpdate_EmitsUsageEvent()
    {
        var usageDetails = new UsageDetails
        {
            InputTokenCount = 500,
            OutputTokenCount = 200,
            CachedInputTokenCount = 100,
        };

        var (sut, _) = CreateSutWithMockedRunner(writer =>
        {
            writer.TryWrite(new AgentResponseUpdate
            {
                Contents = { new UsageContent(usageDetails), new TextContent("response") }
            });
        });

        var events = await CollectEventsAsync(sut);

        var usageEvents = events.OfType<UsageUpdateEvent>().ToList();
        usageEvents.Should().HaveCount(1, "usage update should be emitted exactly once");
        usageEvents[0].Usage.InputTokens.Should().Be(500);
        usageEvents[0].Usage.OutputTokens.Should().Be(200);
    }

    [Fact]
    public async Task StreamQueryAsync_EmitsTextDeltaAfterToolResult_TurnBoundaryDetected()
    {
        var (sut, _) = CreateSutWithMockedRunner(writer =>
        {
            // Tool call + result
            writer.TryWrite(new AgentResponseUpdate
            {
                Contents = { new FunctionCallContent("call_tb_1", "Read", null) }
            });
            writer.TryWrite(new AgentResponseUpdate
            {
                Contents = { new FunctionResultContent("call_tb_1", "file content") }
            });
            // Text after tool result = new turn
            writer.TryWrite(new AgentResponseUpdate
            {
                Contents = { new TextContent("Based on the file...") }
            });
        });

        var events = await CollectEventsAsync(sut);

        // Should have at least one ToolStartEvent and ToolDoneEvent
        events.OfType<ToolStartEvent>().Should().HaveCount(1);
        events.OfType<ToolDoneEvent>().Should().HaveCount(1);

        // Text should be emitted after tool results (turn boundary)
        var textDeltas = events.OfType<TextDeltaEvent>().ToList();
        textDeltas.Should().NotBeEmpty("text after tool results should be emitted as TextDeltaEvent");
        textDeltas[0].Text.Should().Contain("Based on the file");

        // A TurnStartedEvent should be emitted for the new turn
        var turnStarts = events.OfType<TurnStartedEvent>().ToList();
        turnStarts.Should().NotBeEmpty("turn boundary should be detected when text follows tool results");
    }

    [Fact]
    public async Task StreamQueryAsync_DirectBuildConversation_DoesNotCreateBuildRun()
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
        var runner = Substitute.For<IMainAgentRunner>();
        runner.RunStreamingAsync(
                Arg.Any<MainAgentRunOptions>(),
                Arg.Any<ChannelWriter<object>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callInfo.ArgAt<ChannelWriter<object>>(1).TryComplete();
                return Task.FromResult(new MainAgentRunResult(null, 0, 0, 1));
            });
        var sut = CreateSutWithDependencies(runner, sessionManager, coordinator);

        var events = new List<QueryEvent>();
        await foreach (var evt in sut.StreamQueryAsync(
            "解释一下 BuildRun 是什么",
            "sys",
            "model1",
            ct: TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        events.Should().NotContain(item => item is BuildRunStateEvent);
        await coordinator.DidNotReceiveWithAnyArgs().BeginOrResumeAsync(
            default,
            default!,
            default!,
            default,
            default,
            default);
        await runner.Received(1).RunStreamingAsync(
            Arg.Any<MainAgentRunOptions>(),
            Arg.Any<ChannelWriter<object>>(),
            Arg.Any<CancellationToken>());
    }

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
            var questionGenerator = Substitute.For<IClarificationQuestionGenerator>();
            questionGenerator.GenerateAsync(
                    Arg.Any<string>(),
                    Arg.Any<OneCode.Core.Build.RequirementAssessment>(),
                    Arg.Any<CancellationToken>())
                .Returns(new RequirementIntake(["第一期要交付什么？"], [], [], []));
            var coordinator = new BuildRunCoordinator(
                buildStore,
                fingerprint,
                new RequirementAssessmentService(),
                new BuildStateTransitionService(),
                new TaskService(),
                questionGenerator,
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
            var runner = Substitute.For<IMainAgentRunner>();
            runner.RunStreamingAsync(
                    Arg.Any<MainAgentRunOptions>(),
                    Arg.Any<ChannelWriter<object>>(),
                    Arg.Any<CancellationToken>())
                .Returns(async callInfo =>
                {
                    var options = callInfo.ArgAt<MainAgentRunOptions>(0);
                    var writer = callInfo.ArgAt<ChannelWriter<object>>(1);
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
                        TerminalReason: BuildTerminalReason.Completed,
                        FinalValidationStatus: BuildValidationStatus.Passed);
                });
            var clarifier = Substitute.For<IClarificationInteractionService>();
            clarifier.AskAsync(
                    Arg.Any<string>(),
                    Arg.Any<IReadOnlyList<string>>(),
                    Arg.Any<bool>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ClarificationInteractionResult("确认执行", false)));
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
                clarifier,
                capabilityResolver);
            var request = new WorkflowRunRequest(
                "durable-build-run",
                conversation.Id,
                "Fix Foo.cs line 42 and run FooTests.",
                "system",
                "model1",
                WorkingMode.Build,
                root);

            var events = new List<QueryEvent>();
            await foreach (var evt in sut.StreamWorkflowRunAsync(
                request,
                TestContext.Current.CancellationToken))
            {
                events.Add(evt);
            }

            var persisted = await buildStore.LoadAsync(conversation.Id, TestContext.Current.CancellationToken);
            persisted!.State.Should().Be(BuildRunState.Completed);
            persisted.WorkflowFencingToken.Should().BeGreaterThan(0);
            persisted.Metrics.TurnsCompleted.Should().Be(1);
            persisted.ApprovedToolPolicy.Should().NotBeNull();
            persisted.PlanApprovalSource.Should().Be("runtime-approved");
            events.OfType<BuildRunStateEvent>().Select(item => item.State).Should().ContainInOrder(
                BuildRunState.Planning,
                BuildRunState.Planned,
                BuildRunState.Implementing,
                BuildRunState.Verifying,
                BuildRunState.Accepting,
                BuildRunState.Completed);
            events.OfType<TextDeltaEvent>().Should().ContainSingle().Which.Text.Should().Be("durable complete");
            events.OfType<BuildRunCompletedEvent>().Should().ContainSingle();
            events.OfType<DoneEvent>().Should().ContainSingle().Which.TerminalReason
                .Should().Be(BuildTerminalReason.Completed);
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
            TerminalReason = BuildTerminalReason.Completed,
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
            conversation.WorkingDirectory);

        var events = new List<QueryEvent>();
        await foreach (var evt in sut.StreamWorkflowRunAsync(
            request,
            TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        events.OfType<BuildRunCompletedEvent>().Should().ContainSingle();
        events.OfType<DoneEvent>().Should().ContainSingle()
            .Which.TerminalReason.Should().Be(BuildTerminalReason.Completed);
        await runner.DidNotReceive().RunStreamingAsync(
            Arg.Any<MainAgentRunOptions>(),
            Arg.Any<ChannelWriter<object>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamWorkflowRunAsync_PlanApprovalCancelled_BlocksRunWithoutStartingAgent()
    {
        var root = Path.Combine(Path.GetTempPath(), "onecode-chat-cancel-" + Guid.NewGuid().ToString("N"));
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
                .Returns(Task.FromResult("fingerprint-cancel"));
            var questionGenerator = Substitute.For<IClarificationQuestionGenerator>();
            questionGenerator.GenerateAsync(
                    Arg.Any<string>(),
                    Arg.Any<OneCode.Core.Build.RequirementAssessment>(),
                    Arg.Any<CancellationToken>())
                .Returns(new RequirementIntake(["第一期要交付什么？"], [], [], []));
            var coordinator = new BuildRunCoordinator(
                buildStore,
                fingerprint,
                new RequirementAssessmentService(),
                new BuildStateTransitionService(),
                new TaskService(),
                questionGenerator,
                Substitute.For<ILogger<BuildRunCoordinator>>());
            var clarifier = Substitute.For<IClarificationInteractionService>();
            clarifier.AskAsync(
                    Arg.Any<string>(),
                    Arg.Any<IReadOnlyList<string>>(),
                    Arg.Any<bool>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(ClarificationInteractionResult.Cancelled));
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
            var runner = Substitute.For<IMainAgentRunner>();
            var sut = CreateSutWithDependencies(
                runner,
                sessionManager,
                coordinator,
                clarificationInteraction: clarifier,
                capabilityResolver: capabilityResolver);
            var request = new WorkflowRunRequest(
                "cancelled-approval",
                conversation.Id,
                "Fix Foo.cs line 42 and run FooTests.",
                "system",
                "model1",
                WorkingMode.Build,
                root);

            var events = new List<QueryEvent>();
            await foreach (var evt in sut.StreamWorkflowRunAsync(
                request,
                TestContext.Current.CancellationToken))
            {
                events.Add(evt);
            }

            var persisted = await buildStore.LoadAsync(conversation.Id, TestContext.Current.CancellationToken);
            persisted!.State.Should().Be(BuildRunState.Blocked);
            persisted.PlanRejectionReason.Should().Contain("取消");
            persisted.ApprovedToolPolicy.Should().BeNull();
            events.OfType<BuildRunStateEvent>().Select(item => item.State).Should().Contain(BuildRunState.Blocked);
            events.OfType<DoneEvent>().Should().ContainSingle();
            await runner.DidNotReceive().RunStreamingAsync(
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

    // Helpers

    private static async Task<MainAgentRunResult> WaitForCancellationAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("An infinite delay must only complete through cancellation.");
    }

    private static async Task<List<QueryEvent>> CollectEventsAsync(ChatService sut, CancellationToken ct = default)
    {
        var events = new List<QueryEvent>();
        await foreach (var evt in sut.StreamQueryAsync("test", "sys", "model1", ct: ct))
            events.Add(evt);
        return events;
    }

    private static ChatService CreateSutWithDependencies(
        IMainAgentRunner runner,
        ISessionManager sessionManager,
        IBuildRunCoordinator coordinator,
        ControlledBuildAttemptHost? controlledBuildAttemptHost = null,
        IBuildRunStore? buildRunStore = null,
        IClarificationInteractionService? clarificationInteraction = null,
        IToolCapabilityResolver? capabilityResolver = null)
    {
        var toolMetadata = new ToolMetadataRegistry();
        var toolCatalog = new ToolCatalog(
            new Lazy<List<AIFunction>>(() => []),
            toolMetadata,
            mcpConnectionManager: null);
        var configManager = TestConfigManager.Create();
        configManager.Current.Returns(ConfigSnapshot.FromEffective(new AppSettings()));
        return new ChatService(
            Substitute.For<ILogger<ChatService>>(),
            runner,
            toolCatalog,
            Substitute.For<IHookExecutionService>(),
            new ChatSessionDependencies(
                sessionManager,
                new SessionToolSetManager(toolCatalog, toolMetadata),
                capabilityResolver ?? new ToolCapabilityResolver(toolCatalog),
                configManager,
                BuildRunCoordinator: coordinator,
                ControlledBuildAttemptHost: controlledBuildAttemptHost,
                BuildRunStore: buildRunStore,
                ClarificationInteraction: clarificationInteraction),
            new ChatObservabilityDependencies(
                Substitute.For<ITokenUsageTracker>(),
                new TokenBreakdownEstimator(TestSupport.TestTokenEstimators.Default),
                Substitute.For<INotifierService>()));
    }

    private static (ChatService Sut, IMainAgentRunner Runner) CreateSutWithMockedRunner(
        Action<ChannelWriter<object>>? writeUpdates = null)
    {
        var logger = Substitute.For<ILogger<ChatService>>();
        var toolMetadata = new ToolMetadataRegistry();
        var toolCatalog = new ToolCatalog(new Lazy<List<AIFunction>>(() => []), toolMetadata, mcpConnectionManager: null);
        var configManager = TestConfigManager.Create();
        configManager.Current.Returns(ConfigSnapshot.FromEffective(new AppSettings()));
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(Path.GetTempPath());

        var runner = Substitute.For<IMainAgentRunner>();
        runner.RunStreamingAsync(
            Arg.Any<MainAgentRunOptions>(),
            Arg.Do<ChannelWriter<object>>(writer =>
            {
                writeUpdates?.Invoke(writer);
                writer.Complete();
            }),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MainAgentRunResult(
                Text: null,
                TotalInputTokens: 0,
                TotalOutputTokens: 0,
                TurnCount: 0)));

        var sut = new ChatService(logger,
            mainAgentRunner: runner,
            toolCatalog: toolCatalog,
            hookExecutionService: Substitute.For<IHookExecutionService>(),
            session: new ChatSessionDependencies(
                sessionManager,
                new SessionToolSetManager(toolCatalog, toolMetadata),
                new ToolCapabilityResolver(toolCatalog),
                configManager),
            observability: new ChatObservabilityDependencies(
                Substitute.For<ITokenUsageTracker>(),
                new TokenBreakdownEstimator(TestSupport.TestTokenEstimators.Default),
                Substitute.For<INotifierService>()));

        return (sut, runner);
    }

    private static ChatService CreateSut()
    {
        var logger = Substitute.For<ILogger<ChatService>>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var toolMetadata = new ToolMetadataRegistry();
        var toolCatalog = new ToolCatalog(new Lazy<List<AIFunction>>(() => []), toolMetadata, mcpConnectionManager: null);
        // MainAgentRunner is now a required (non-null) dependency of ChatService.
        // Construct a real instance; tests above short-circuit before invoking the runner's pipeline.
        var chatClient = Substitute.For<IChatClient>();
        var configManager = TestConfigManager.Create();
        configManager.Current.Returns(ConfigSnapshot.FromEffective(new AppSettings()));
        var modeProvider = new PermissionModeProvider(configManager);
        var promptManager = new PromptManager();
        var sessionManager = Substitute.For<ISessionManager>();
        var modelManager = new ModelManager(configManager, new ModelCatalogStore());

        var (_, mainContextBuilder) = TestSupport.TestAgentContextProviderAssembly.Create(
            sessionManager: sessionManager,
            modelManager: modelManager,
            modeProvider: modeProvider,
            promptManager: promptManager,
            planModeService: Substitute.For<IPlanModeService>(),
            planWorkflowService: Substitute.For<IPlanWorkflowApplicationService>());
        var mainAgentRunner = new MainAgentRunner(
            mainContextBuilder,
            new AgentPipelineAssembly(
                Substitute.For<IWorkingDirectoryAccessor>(),
                Substitute.For<IHookExecutionService>(),
                Substitute.For<IVerificationProvider>(),
                modeProvider,
                Substitute.For<IPermissionChecker>(),
                new CostTracker()),
            new CompactionProviderBuilder(chatClient, NullLoggerFactory.Instance, modelManager, new OneCode.App.Services.Compact.CompactPromptBuilder(promptManager)),
            new AgentSessionStore(sessionManager, NullLoggerFactory.Instance.CreateLogger<AgentSessionStore>()),
            chatClient,
            NullLoggerFactory.Instance,
            serviceProvider,
            toolMetadata);

        return new ChatService(logger,
            mainAgentRunner: mainAgentRunner,
            toolCatalog: toolCatalog,
            hookExecutionService: Substitute.For<IHookExecutionService>(),
            session: new ChatSessionDependencies(
                sessionManager,
                new SessionToolSetManager(toolCatalog, toolMetadata),
                new ToolCapabilityResolver(toolCatalog),
                configManager),
            observability: new ChatObservabilityDependencies(
                Substitute.For<ITokenUsageTracker>(),
                new TokenBreakdownEstimator(TestSupport.TestTokenEstimators.Default),
                Substitute.For<INotifierService>()));
    }
}

