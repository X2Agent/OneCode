
using OneCode.Core.Config;
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
using OneCode.Core.Build;
using OneCode.Core.Domain;
using OneCode.Core.Hooks;
using OneCode.Core.Models;
using OneCode.Core.Permissions;
using OneCode.Core.Prompt;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Api;
using OneCode.Tests.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
namespace OneCode.Tests;

public sealed partial class QueryStreamEngineTests
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
    public async Task StreamQueryAsync_FirstQueryRunStart_PopulatesCacheSafeParamsBeforeForkCanRead()
    {
        // AgentTool 在会话首个查询期间发起 fork 时读取 ICacheSafeParamsProvider.Current；
        // 旧实现仅在查询完成后的 FinalizeAsync 才写入 → 首轮 fork 拿到 null →
        // ForkedAgentRunner.FilterChildTools 得到空工具表（子代理报"没有 WebSearch"）。
        // 捕获时机 = runner 被调用（StartRun）时刻，即 fork 发起时刻的可见性。
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(Path.GetTempPath());
        var webSearchTool = AIFunctionFactory.Create(() => "results", name: "WebSearch");

        QueryStreamEngine engine = null!;
        CacheSafeParams? seenByFork = null;
        var runner = Substitute.For<IMainAgentRunner>();
        runner.RunStreamingAsync(
                Arg.Any<MainAgentRunOptions>(),
                Arg.Do<ChannelWriter<object>>(writer =>
                {
                    seenByFork = engine.Current;
                    writer.Complete();
                }),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MainAgentRunResult(
                Text: null, TotalInputTokens: 0, TotalOutputTokens: 0, TurnCount: 0)));

        engine = CreateSutWithDependencies(
            runner,
            sessionManager,
            Substitute.For<IBuildRunCoordinator>(),
            extraTools: [webSearchTool],
            registerExtraToolMetadata: reg => reg.Register(new ToolMetadata { Name = "WebSearch", Risk = ToolRisk.ReadOnly }));

        await CollectEventsAsync(engine);

        seenByFork.Should().NotBeNull(
            "a fork issued during the first query reads Current at run start; it must be populated before StartRun");
        // 测试配置默认开启 NextPromptSuggester，SystemPrompt 会被追加引导段——按前缀断言。
        seenByFork.SystemPrompt.Should().StartWith("sys");
        seenByFork.ModelId.Should().Be("model1");
        seenByFork.Tools.Should().NotBeNull("an empty tool list is what starved the sub-agent of WebSearch");
        seenByFork.Tools.Should().Contain(t => t.Name == "WebSearch");
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
    public async Task StreamWorkflowRunAsync_RecordedSeedBoundary_FlowsIntoRunOptions()
    {
        var sessionId = SessionId.NewId();
        var conversation = new Conversation
        {
            Id = sessionId,
            Name = "boundary",
            WorkingDirectory = Path.GetTempPath(),
            Model = "model",
        };
        HistorySeedBoundary.RecordForExecution(conversation, "req-1", messageCount: 5);
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(Path.GetTempPath());
        sessionManager.GetConversation(sessionId).Returns(conversation);
        var runner = Substitute.For<IMainAgentRunner>();
        MainAgentRunOptions? capturedOptions = null;
        runner.RunStreamingAsync(
                Arg.Do<MainAgentRunOptions>(options => capturedOptions = options),
                Arg.Do<ChannelWriter<object>>(writer => writer.Complete()),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MainAgentRunResult(
                Text: null, TotalInputTokens: 0, TotalOutputTokens: 0, TurnCount: 0)));
        var sut = CreateSutWithDependencies(
            runner,
            sessionManager,
            Substitute.For<IBuildRunCoordinator>());
        var request = new WorkflowRunRequest(
            "plan-run-boundary",
            sessionId,
            "execute",
            "system",
            "model",
            WorkingMode.Plan,
            Path.GetTempPath(),
            PrescribedPlan());

        await foreach (var _ in sut.StreamWorkflowRunAsync(request, TestContext.Current.CancellationToken)) { }

        capturedOptions.Should().NotBeNull();
        capturedOptions!.HistorySeedBoundary.Should().Be(new HistorySeedBoundary("req-1", 0, 5),
            "the recorded boundary must reach the run options so the history provider seeds after it");
    }

    [Fact]
    public async Task StreamWorkflowRunAsync_EpochDriftedBoundary_FallsBackToFullSeed()
    {
        var sessionId = SessionId.NewId();
        var conversation = new Conversation
        {
            Id = sessionId,
            Name = "boundary-drifted",
            WorkingDirectory = Path.GetTempPath(),
            Model = "model",
        };
        HistorySeedBoundary.RecordForExecution(conversation, "req-1", messageCount: 5);
        // 边界记录后发生 compact/clear：结构变更让边界失效，执行轮回退全量播种
        conversation.Metadata[MafSessionInvalidator.HistoryEpochKey] = 1;
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(Path.GetTempPath());
        sessionManager.GetConversation(sessionId).Returns(conversation);
        var runner = Substitute.For<IMainAgentRunner>();
        MainAgentRunOptions? capturedOptions = null;
        runner.RunStreamingAsync(
                Arg.Do<MainAgentRunOptions>(options => capturedOptions = options),
                Arg.Do<ChannelWriter<object>>(writer => writer.Complete()),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MainAgentRunResult(
                Text: null, TotalInputTokens: 0, TotalOutputTokens: 0, TurnCount: 0)));
        var sut = CreateSutWithDependencies(
            runner,
            sessionManager,
            Substitute.For<IBuildRunCoordinator>());
        var request = new WorkflowRunRequest(
            "plan-run-boundary-drifted",
            sessionId,
            "execute",
            "system",
            "model",
            WorkingMode.Plan,
            Path.GetTempPath(),
            PrescribedPlan());

        await foreach (var _ in sut.StreamWorkflowRunAsync(request, TestContext.Current.CancellationToken)) { }

        capturedOptions.Should().NotBeNull();
        capturedOptions!.HistorySeedBoundary.Should().BeNull(
            "a boundary recorded before a structural history change must not truncate the seed");
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
        IToolCapabilityResolver? capabilityResolver = null,
        IReadOnlyList<AIFunction>? extraTools = null,
        Action<ToolMetadataRegistry>? registerExtraToolMetadata = null)
    {
        var toolMetadata = new ToolMetadataRegistry();
        registerExtraToolMetadata?.Invoke(toolMetadata);
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
                BuildRunStore: buildRunStore),
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





