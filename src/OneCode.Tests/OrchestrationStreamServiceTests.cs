using Microsoft.Agents.AI.Workflows;
using NSubstitute;
using OneCode.App.Services;
using OneCode.App.Services.Agent;
using OneCode.App.Services.GoalMode;
using OneCode.App.Services.Streaming;
using OneCode.App.Query;
using OneCode.App.Session;
using OneCode.Core.Build;
using OneCode.Core.Config;
using OneCode.Core.Coordinator;
using OneCode.Core.Goals;
using OneCode.Core.Tokens;
using OneCode.Core.Tools;
using System.Threading.Channels;
using OneCode.Infrastructure.Api;
using OneCode.Infrastructure.Goals;
using System.Text.Json;

namespace OneCode.Tests;

/// <summary>
/// /resume 端到端回归：StreamGoalAsync（Begin）持久化的 DefinitionHash 必须能被
/// StreamResumeGoalAsync 的校验复算通过。hash 契约含「显式 JsonSerializerOptions」
/// 签名成分——resume 侧漏传 options 时两形式必然失配，恢复流恒抛 definition changed。
/// </summary>
public sealed class OrchestrationStreamServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onecode-goal-stream-tests", Guid.NewGuid().ToString("N"));

    private const string ModelId = "model-a";
    private const string SystemPrompt = "goal system prompt";

    [Fact]
    public async Task Resume_AcceptsHashPersistedByBegin()
    {
        var sessionId = SessionId.NewId();
        var run = await BeginRunAsync(sessionId);

        var sut = CreateSut(resolvedRun: run);
        var events = new List<QueryEvent>();
        var drain = async () =>
        {
            await foreach (var evt in sut.StreamResumeGoalAsync(sessionId, ModelId, SystemPrompt, TestContext.Current.CancellationToken))
                events.Add(evt);
        };

        // 校验通过即进入执行路径；宿主为纯桩，异常被收入 ErrorEvent，绝不复现 definition changed。
        await drain.Should().NotThrowAsync();
        events.OfType<ErrorEvent>().Select(e => e.Message)
            .Should().NotContain(t => t.Contains("definition changed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Resume_RejectsHashPersistedWithoutSerializerOptions()
    {
        var sessionId = SessionId.NewId();
        var run = await BeginRunAsync(sessionId);
        // 反证：把持久化 hash 换成「未传 options」形式（default 字面量签名），校验必须拒绝。
        var mismatched = run with
        {
            DefinitionHash = GoalWorkflowCompiler.ComputeDefinitionHash(
                run, ModelId, GoalWorkflowCompiler.ComputeTextHash(SystemPrompt), ToolCapabilityHash()),
        };

        var sut = CreateSut(resolvedRun: mismatched);
        var drain = async () =>
        {
            await foreach (var _ in sut.StreamResumeGoalAsync(sessionId, ModelId, SystemPrompt, TestContext.Current.CancellationToken))
            { }
        };

        await drain.Should().ThrowAsync<InvalidOperationException>().WithMessage("*definition changed*");
    }

    [Fact]
    public async Task ForwardOrchestrationEvent_TextDelta_WritesExactlyOneTextEvent()
    {
        var channel = Channel.CreateUnbounded<QueryEvent>();

        OrchestrationStreamService.ForwardOrchestrationEvent(
            new OrchestrationEvent.TextDelta("goal", "partial answer"),
            channel.Writer);
        channel.Writer.Complete();

        var events = new List<QueryEvent>();
        await foreach (var evt in channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
            events.Add(evt);

        events.Should().ContainSingle().Which.Should().BeOfType<TextDeltaEvent>()
            .Which.Text.Should().Be("partial answer");
    }

    [Fact]
    public void BuildDoneUsage_ExistingSessionBucket_ReturnsGenerationDelta()
    {
        var ledger = new TokenLedger();
        var sessionId = SessionId.NewId();
        ledger.RecordUsage(sessionId, new UsageRecord(ModelId, 100, 20, 10, 5));
        var baseline = ledger.GetSessionUsage(sessionId)!.Snapshot();
        ledger.RecordUsage(sessionId, new UsageRecord(ModelId, 40, 8, 4, 2));
        var run = new GoalRun
        {
            Id = GoalRunId.New(),
            SessionId = sessionId,
            Goal = "goal",
            WorkingDirectory = _root,
            WorkspaceFingerprint = "fingerprint",
            DefinitionHash = "hash",
        };

        var usage = OrchestrationStreamService.BuildDoneUsage(
            run, baseline, ledger.GetSessionUsage(sessionId));

        usage.Should().Be(new OneCode.Core.Domain.TokenUsage(40, 8, 4, 2));
    }

    [Fact]
    public async Task Resume_TranscriptFailure_EmitsErrorWithoutDoneOrGoalResult()
    {
        var sessionId = SessionId.NewId();
        var running = (await BeginRunAsync(sessionId)) with { Workspace = null, State = GoalRunState.Executing };
        running = running with
        {
            DefinitionHash = GoalWorkflowCompiler.ComputeDefinitionHash(
                running,
                ModelId,
                GoalWorkflowCompiler.ComputeTextHash(SystemPrompt),
                ToolCapabilityHash(),
                new JsonSerializerOptions()),
        };
        var final = running with { State = GoalRunState.Failed };
        var goalStore = Substitute.For<IGoalRunStore>();
        goalStore.LoadByIdAsync(running.Id, Arg.Any<CancellationToken>()).Returns(running);
        goalStore.ClaimWorkflowAsync(running.Id, Arg.Any<long>(), running.Version, Arg.Any<CancellationToken>())
            .Returns(call => running with
            {
                WorkflowFencingToken = call.ArgAt<long>(1),
                Version = running.Version + 1,
            });
        var sessionManager = Substitute.For<ISessionManager>();
        sessionManager.WorkingDirectory.Returns(_root);
        sessionManager.AppendAssistantMessageAsync(
                Arg.Any<SessionId>(),
                Arg.Any<string>(),
                Arg.Any<OneCode.Core.Domain.TokenUsage?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new IOException("disk full"));
        var interactive = new InteractiveSession(
            null!, SystemPrompt, sessionManager, null!, [], string.Empty);
        var sut = CreateSut(
            running,
            final,
            new FakeDurableWorkflowHost(running.Id, GoalRunState.Failed),
            goalStore);
        var events = new List<QueryEvent>();

        await foreach (var evt in sut.StreamResumeGoalAsync(
            sessionId, ModelId, SystemPrompt, TestContext.Current.CancellationToken, interactive))
        {
            events.Add(evt);
        }

        events.OfType<ErrorEvent>().Should().ContainSingle().Which.Message.Should().Contain("disk full");
        events.OfType<DoneEvent>().Should().BeEmpty();
        events.OfType<GoalResultEvent>().Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private async Task<GoalRun> BeginRunAsync(SessionId sessionId)
    {
        var store = new JsonGoalRunStore(_root);
        var workspace = Substitute.For<IGoalWorkspaceService>();
        var fingerprint = Substitute.For<IWorkspaceFingerprintProvider>();
        fingerprint.ComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("fingerprint-a");
        workspace.PrepareAsync(Arg.Any<GoalRun>(), Arg.Any<CancellationToken>())
            .Returns(call => Workspace(call.ArgAt<GoalRun>(0)));
        var appService = new GoalRunApplicationService(store, workspace, fingerprint);

        return await appService.BeginAsync(
            sessionId, "goal text", _root, ModelId,
            GoalWorkflowCompiler.ComputeTextHash(SystemPrompt),
            ToolCapabilityHash(),
            new JsonSerializerOptions(),
            TestContext.Current.CancellationToken);
    }

    private static string ToolCapabilityHash()
        => GoalWorkflowCompiler.ComputeToolCapabilityHash(Enumerable.Empty<string>());

    private OrchestrationStreamService CreateSut(
        GoalRun? resolvedRun,
        GoalRun? finalRun = null,
        IDurableWorkflowHost? durableHost = null,
        IGoalRunStore? goalStore = null)
    {
        var service = Substitute.For<IGoalRunApplicationService>();
        service.GetBySessionAsync(Arg.Any<SessionId>(), Arg.Any<CancellationToken>()).Returns(resolvedRun);
        service.GetAsync(Arg.Any<GoalRunId>(), Arg.Any<CancellationToken>()).Returns(finalRun ?? resolvedRun);

        var config = Substitute.For<IConfigManager>();
        config.Current.Returns(ConfigSnapshot.FromEffective(new AppSettings()));

        var catalog = Substitute.For<IToolCatalog>();
        catalog.Tools.Returns([]);

        var resolver = Substitute.For<IToolCapabilityResolver>();
        resolver.Resolve(Arg.Any<WorkingMode>())
            .Returns(ToolCapabilitySet.CreateUnrestricted([]));

        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(_root);

        var host = new GoalWorkflowHost(
            durableHost ?? Substitute.For<IDurableWorkflowHost>(),
            new GoalWorkflowCompiler(),
            goalStore ?? Substitute.For<IGoalRunStore>(),
            Substitute.For<IWorkflowRunRegistry>());

        var runtimeFactory = Substitute.For<IGoalWorkflowRuntimeFactory>();
        runtimeFactory.Create(Arg.Any<GoalWorkflowRuntimeContext>()).Returns(Substitute.For<IGoalWorkflowRuntime>());

        return new OrchestrationStreamService(config, catalog, resolver, wd, service, host, runtimeFactory, new TokenLedger());
    }

    private static GoalWorkspaceSnapshot Workspace(GoalRun run) => new(
        $"goal-{run.Id}",
        run.WorkingDirectory,
        Path.Combine(Path.GetDirectoryName(run.WorkingDirectory)!, $"{Path.GetFileName(run.WorkingDirectory)}.worktree", run.Id.Value),
        $"onecode/goal/{run.Id}",
        "main",
        "base-head",
        run.WorkspaceFingerprint,
        DateTimeOffset.UtcNow);

    private sealed class FakeDurableWorkflowHost(GoalRunId runId, GoalRunState finalState) : IDurableWorkflowHost
    {
        public async Task<DurableWorkflowRunResult> RunAsync<TInput>(
            WorkflowRunRegistration registration,
            Workflow workflow,
            TInput input,
            string commandId,
            JsonSerializerOptions serializerOptions,
            Func<WorkflowRuntimeEvent, CancellationToken, ValueTask>? eventSink = null,
            int? executionGeneration = null,
            Func<WorkflowRunRecord, CancellationToken, ValueTask>? leaseAcquired = null,
            Func<IReadOnlyList<WorkflowRuntimeEvent>, WorkflowRunState?>? terminalStateResolver = null,
            ExternalResponse? externalResponse = null,
            CancellationToken ct = default)
        {
            var record = new WorkflowRunRecord(
                registration.RunId,
                registration.RunKind,
                registration.DefinitionHash,
                7,
                WorkflowRunState.Active,
                null,
                1,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                ExecutionGeneration: executionGeneration ?? 1);
            if (leaseAcquired is not null)
                await leaseAcquired(record, ct);

            IReadOnlyList<WorkflowRuntimeEvent> events =
            [
                new WorkflowRuntimeEvent.Output(
                    registration.RunId,
                    "goal",
                    new GoalWorkflowOutput(runId, finalState, 0, 1, "validation failed")),
            ];
            var terminalState = terminalStateResolver?.Invoke(events) ?? WorkflowRunState.Completed;
            return new DurableWorkflowRunResult(record with { State = terminalState }, false, false, events);
        }
    }
}
