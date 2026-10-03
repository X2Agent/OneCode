using OneCode.Core.Config;
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
using OneCode.App.Services.Streaming;
using OneCode.App.Session;
using OneCode.App.Tools;
using OneCode.Core.Build;
using OneCode.Core.Workflows;
using OneCode.Core.Coordinator;
using OneCode.Core.Domain;
using OneCode.Core.Hooks;
using OneCode.Core.Models;
using OneCode.Core.Permissions;
using OneCode.Core.PlanMode;
using OneCode.Core.Prompt;
using OneCode.Core.Tools;
using OneCode.Core.Tasks;
using OneCode.Infrastructure.Api;
using OneCode.Infrastructure.Build;
using OneCode.Infrastructure.Workflows;
using OneCode.Tests.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace OneCode.Tests;

public sealed class QueryStreamEngineTests
{
    /// <summary>受控执行请求的预置计划固定样本（内容不参与断言）。</summary>
    private static BuildPlan PrescribedPlan()
        => new(
            "Fix Foo.cs and verify",
            [new BuildPlanTask("t1", "Fix Foo.cs line 42", "Fix and verify", [], [], ["FooTests passes"])],
            ["dotnet test"],
            [],
            []);

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
            Path.GetTempPath(),
            PrescribedPlan());
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
            // SUT 取消路径经 PersistCancelledRunAsync 特意用 None 持久化（落库不可再被取消），故此处匹配 None。
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
            Path.GetTempPath(),
            PrescribedPlan());
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
        turnStarts.Should().HaveCount(1, "turn boundary should be detected exactly once when text follows tool results");
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
            var taskService = new TaskService();
            var coordinator = new BuildRunCoordinator(
                buildStore,
                fingerprint,
                new RequirementAssessmentService(),
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
            // 批准执行要求 step 证据经 UpdatePlanStep 提交（BuildProtocolBlock 协议）——
            // seed 一个 Executing 态 PlanWorkflow 并把真工具注册进目录，stub agent 据此提交。
            var runId = "durable-build-run";
            var planStore = new PlanAggregateStore(Path.Combine(root, "plan-aggregates"));
            var workflowService = new PlanWorkflowApplicationService(planStore);
            var now = DateTimeOffset.UtcNow;
            var workflow = PlanWorkflow.Create(conversation.Id) with
            {
                State = PlanWorkflowState.Executing,
                Version = 1,
                LatestRevision = 1,
                SubmittedRevision = 1,
                ApprovedRevision = 1,
                ExecutionRequestId = "durable",
                ActiveRunId = runId,
                ActiveRunKind = PlanRunKind.Build,
                ApprovedSnapshot = new ApprovedPlanSnapshot
                {
                    PlanId = default,
                    SessionId = conversation.Id,
                    Revision = 1,
                    Title = "Durable plan",
                    Markdown = "# Durable plan",
                    Steps = [new PlanStepDefinition
                    {
                        Id = "t1",
                        Title = "Fix Foo.cs",
                        Description = "Fix and verify",
                        Files = [],
                        AcceptanceCriteria = ["FooTests passes"],
                        DependsOn = [],
                        Risk = PlanStepRisk.Low,
                    }],
                    ContentHash = "sha256-durable",
                    ApprovedBy = "user",
                    ApprovedAt = now,
                },
                StepExecutions = [new PlanStepExecution
                {
                    StepId = "t1",
                    Status = PlanStepExecutionStatus.InProgress,
                    UpdatedAt = now,
                }],
                UpdatedAt = now,
            };
            await planStore.SaveAsync(
                new PlanAggregate(workflow, [new PlanRevision
                {
                    PlanId = workflow.Id,
                    SessionId = conversation.Id,
                    Revision = 1,
                    Title = "Durable plan",
                    Markdown = "# Durable plan",
                    Steps = workflow.ApprovedSnapshot!.Steps,
                    Risks = [],
                    Assumptions = [],
                    ContentHash = "sha256-durable",
                    Status = PlanRevisionStatus.Approved,
                    CreatedAt = now,
                }]),
                -1,
                TestContext.Current.CancellationToken);
            var planExecutionTool = new PlanExecutionTool(
                workflowService,
                new PlanCardPublisher(new OrchestrationEventBus()),
                new BuildTaskLinker(taskService));
            var updateStepTool = AIFunctionFactory.Create(
                planExecutionTool.UpdatePlanStepAsync, name: PlanToolNames.UpdateStep);
            var runner = Substitute.For<IMainAgentRunner>();
            runner.RunStreamingAsync(
                    Arg.Any<MainAgentRunOptions>(),
                    Arg.Any<ChannelWriter<object>>(),
                    Arg.Any<CancellationToken>())
                .Returns(async callInfo =>
                {
                    var options = callInfo.ArgAt<MainAgentRunOptions>(0);
                    var writer = callInfo.ArgAt<ChannelWriter<object>>(1);
                    // 按 BuildProtocolBlock 协议提交 step 证据（ambient 上下文已由
                    // attempt 运行时重建，工具能定位会话与 run）。
                    var updateStep = options.Tools!.OfType<AIFunction>()
                        .Single(t => t.Name == PlanToolNames.UpdateStep);
                    await updateStep.InvokeAsync(new AIFunctionArguments
                    {
                        ["stepId"] = "t1",
                        ["status"] = "completed",
                        ["evidence"] = "Foo.cs fixed; FooTests passed.",
                    }, TestContext.Current.CancellationToken);
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
            var clarifier = Substitute.For<IClarificationInteractionService>();
            clarifier.AskAsync(
                    Arg.Any<string>(),
                    Arg.Any<IReadOnlyList<string>>(),
                    Arg.Any<bool>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ClarificationInteractionResult("确认执行", false)));
            var capabilities = new ToolCapabilitySet
            {
                AllowedToolNames = new[] { "ReadFile", "Write", "Edit", PlanToolNames.UpdateStep }.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
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
                capabilityResolver,
                extraTools: [updateStepTool]);
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
                BuildRunState.Planning,
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

    /// <summary>
    /// Plan-mode interactive run bridge: SubmitPlan (executed inside the agent run)
    /// persists FinalizingPlanRun; when the run stream completes, the engine must
    /// transition the workflow to AwaitingApproval and publish it — this is the
    /// event that pops the approval InlineSelector in the TUI.
    /// </summary>
    [Fact]
    public async Task StreamQueryAsync_PlanRunWithSubmittedPlan_TransitionsToAwaitingApprovalOnCompletion()
    {
        var root = Path.Combine(Path.GetTempPath(), "onecode-plan-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new PlanAggregateStore(root);
            var workflowService = new PlanWorkflowApplicationService(store);
            var bus = new OrchestrationEventBus();
            var publisher = new PlanCardPublisher(bus);
            var publishedStates = new List<PlanWorkflowState>();
            bus.Subscribe(evt =>
            {
                if (evt is OrchestrationEvent.PlanProjectionChanged changed)
                    publishedStates.Add(changed.Workflow.State);
            });

            var conversation = new Conversation { Id = SessionId.NewId(), WorkingDirectory = root };
            var sessionManager = Substitute.For<ISessionManager>();
            sessionManager.ForegroundConversation.Returns(conversation);
            sessionManager.WorkingDirectory.Returns(root);
            sessionManager.GetChatHistory(conversation.Id).Returns(new List<ChatMessage>());

            var planMode = Substitute.For<IPlanModeService>();
            planMode.IsInPlanMode.Returns(true);
            var createPlanTool = new CreatePlanTool(planMode, publisher, sessionManager, workflowService, store);

            string? capturedRunId = null;
            bool? submitFailed = null;
            var runner = Substitute.For<IMainAgentRunner>();
            runner.RunStreamingAsync(
                    Arg.Any<MainAgentRunOptions>(),
                    Arg.Do<ChannelWriter<object>>(writer =>
                    {
                        writer.TryWrite(new AgentResponseUpdate { Contents = { new TextContent("计划卡片已就绪") } });
                        writer.Complete();
                    }),
                    Arg.Any<CancellationToken>())
                .Returns(async _ =>
                {
                    // Simulate the LLM calling SubmitPlan inside the agent run —
                    // OneCodeAgentRunContext.CurrentRunId must be set by the engine here.
                    capturedRunId = OneCodeAgentRunContext.CurrentRunId;
                    var result = await createPlanTool.SubmitPlanAsync(CreatePlanBridgeMarkdown(), [CreatePlanBridgeStep()]);
                    submitFailed = result.IsError;
                    return new MainAgentRunResult(Text: "done", TotalInputTokens: 0, TotalOutputTokens: 0, TurnCount: 1);
                });

            var toolMetadata = new ToolMetadataRegistry();
            var toolCatalog = new ToolCatalog(new Lazy<List<AIFunction>>(() => []), toolMetadata, mcpConnectionManager: null);
            var configManager = TestConfigManager.Create();
            configManager.Current.Returns(ConfigSnapshot.FromEffective(new AppSettings()));
            var sut = new QueryStreamEngine(
                Substitute.For<ILogger<QueryStreamEngine>>(),
                runner,
                toolCatalog,
                Substitute.For<IHookExecutionService>(),
                new ChatSessionDependencies(
                    sessionManager,
                    new SessionToolSetManager(toolCatalog, toolMetadata),
                    new ToolCapabilityResolver(toolCatalog),
                    configManager,
                    PlanWorkflow: workflowService,
                    PlanCardPublisher: publisher),
                new ChatObservabilityDependencies(
                    Substitute.For<ITokenUsageTracker>(),
                    new TokenBreakdownEstimator(TestSupport.TestTokenEstimators.Default),
                    Substitute.For<INotifierService>()));

            await foreach (var _ in sut.StreamQueryAsync(
                "请规划重构方案", "system", "model1",
                ct: TestContext.Current.CancellationToken,
                workingMode: WorkingMode.Plan))
            {
            }

            capturedRunId.Should().NotBeNull("SubmitPlan must execute inside the engine's activation context");
            submitFailed.Should().BeFalse("SubmitPlan should succeed in plan mode with a valid plan");

            var workflow = await workflowService.GetAsync(conversation.Id, TestContext.Current.CancellationToken);
            workflow!.State.Should().Be(PlanWorkflowState.AwaitingApproval,
                "run completion must open approval (FinalizingPlanRun → AwaitingApproval)");
            publishedStates.Should().Contain(PlanWorkflowState.AwaitingApproval,
                "the AwaitingApproval workflow must be published so the TUI pops the approval selector");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static string CreatePlanBridgeMarkdown() =>
        "# Plan Bridge Workflow\n\n## Context\nUpdate src/OneCode.App/Tools/CreatePlanTool.cs using the persisted workflow.\n\n## Approach\nStore immutable revisions and execute approved structured steps.\n\n## Verification\nRun dotnet build and dotnet test.";

    private static PlanStepDto CreatePlanBridgeStep() => new()
    {
        Id = "bridge-workflow",
        Title = "Bridge workflow",
        Description = "Bridge the workflow and immutable revision.",
        Files = ["src/OneCode.App/Tools/CreatePlanTool.cs"],
        AcceptanceCriteria = ["Workflow restores after restart."],
        DependsOn = [],
        Risk = "Low",
    };

    // Helpers

    private static async Task<MainAgentRunResult> WaitForCancellationAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("An infinite delay must only complete through cancellation.");
    }

    private static async Task<List<QueryEvent>> CollectEventsAsync(QueryStreamEngine sut, CancellationToken ct = default)
    {
        var events = new List<QueryEvent>();
        await foreach (var evt in sut.StreamQueryAsync("test", "sys", "model1", ct: ct))
            events.Add(evt);
        return events;
    }

    private static QueryStreamEngine CreateSutWithDependencies(
        IMainAgentRunner runner,
        ISessionManager sessionManager,
        IBuildRunCoordinator coordinator,
        ControlledBuildAttemptHost? controlledBuildAttemptHost = null,
        IBuildRunStore? buildRunStore = null,
        IClarificationInteractionService? clarificationInteraction = null,
        IToolCapabilityResolver? capabilityResolver = null,
        IReadOnlyList<AIFunction>? extraTools = null)
    {
        var toolMetadata = new ToolMetadataRegistry();
        var toolCatalog = new ToolCatalog(
            new Lazy<List<AIFunction>>(() => extraTools?.ToList() ?? []),
            toolMetadata,
            mcpConnectionManager: null);
        var configManager = TestConfigManager.Create();
        configManager.Current.Returns(ConfigSnapshot.FromEffective(new AppSettings()));
        return new QueryStreamEngine(
            Substitute.For<ILogger<QueryStreamEngine>>(),
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

    private static (QueryStreamEngine Sut, IMainAgentRunner Runner) CreateSutWithMockedRunner(
        Action<ChannelWriter<object>>? writeUpdates = null)
    {
        var logger = Substitute.For<ILogger<QueryStreamEngine>>();
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

        var sut = new QueryStreamEngine(logger,
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

    private static QueryStreamEngine CreateSut()
    {
        var logger = Substitute.For<ILogger<QueryStreamEngine>>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var toolMetadata = new ToolMetadataRegistry();
        var toolCatalog = new ToolCatalog(new Lazy<List<AIFunction>>(() => []), toolMetadata, mcpConnectionManager: null);
        // MainAgentRunner is now a required (non-null) dependency of QueryStreamEngine.
        // Construct a real instance; tests above short-circuit before invoking the runner's pipeline.
        var chatClient = Substitute.For<IChatClient>();
        var configManager = TestConfigManager.Create();
        configManager.Current.Returns(ConfigSnapshot.FromEffective(new AppSettings()));
        var modeProvider = new PermissionModeProvider(configManager);
        var promptManager = new PromptManager();
        var sessionManager = Substitute.For<ISessionManager>();
        var modelManager = new ModelManager(configManager, new ModelCatalogStore());

        var (sharedBuilder, mainContextBuilder) = TestSupport.TestAgentContextProviderAssembly.Create(
            sessionManager: sessionManager,
            modelManager: modelManager,
            modeProvider: modeProvider,
            promptManager: promptManager,
            planModeService: Substitute.For<IPlanModeService>(),
            planWorkflowService: Substitute.For<IPlanWorkflowApplicationService>());
        var mainAgentRunner = new MainAgentRunner(
            new AgentContextPipeline(sharedBuilder, mainContextBuilder),
            new AgentPipelineAssembly(
                Substitute.For<IWorkingDirectoryAccessor>(),
                Substitute.For<IHookExecutionService>(),
                Substitute.For<IVerificationProvider>(),
                modeProvider,
                Substitute.For<IPermissionChecker>(),
                new TokenLedger()),
            new CompactionStrategyFactory(chatClient, modelManager, new OneCode.App.Services.Compact.CompactPromptBuilder(promptManager)),
            new AgentSessionPersistence(sessionManager, sessionManager, NullLoggerFactory.Instance.CreateLogger<AgentSessionPersistence>()),
            chatClient,
            NullLoggerFactory.Instance,
            serviceProvider,
            toolMetadata);

        return new QueryStreamEngine(logger,
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
