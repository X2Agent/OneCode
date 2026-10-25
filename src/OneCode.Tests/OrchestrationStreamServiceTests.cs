using NSubstitute;
using OneCode.App.Services.Agent;
using OneCode.App.Services.GoalMode;
using OneCode.App.Services.Streaming;
using OneCode.App.Query;
using OneCode.Core.Build;
using OneCode.Core.Config;
using OneCode.Core.Goals;
using OneCode.Core.Tools;
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

    private OrchestrationStreamService CreateSut(GoalRun? resolvedRun)
    {
        var service = Substitute.For<IGoalRunApplicationService>();
        service.GetBySessionAsync(Arg.Any<SessionId>(), Arg.Any<CancellationToken>()).Returns(resolvedRun);

        var config = Substitute.For<IConfigManager>();
        config.Current.Returns(ConfigSnapshot.FromEffective(new AppSettings()));

        var catalog = Substitute.For<IToolCatalog>();
        catalog.Tools.Returns([]);

        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(_root);

        var host = new GoalWorkflowHost(
            Substitute.For<IDurableWorkflowHost>(),
            new GoalWorkflowCompiler(),
            Substitute.For<IGoalRunStore>(),
            Substitute.For<IWorkflowRunRegistry>());

        var runtimeFactory = Substitute.For<IGoalWorkflowRuntimeFactory>();
        runtimeFactory.Create(Arg.Any<GoalWorkflowRuntimeContext>()).Returns(Substitute.For<IGoalWorkflowRuntime>());

        return new OrchestrationStreamService(config, catalog, wd, service, host, runtimeFactory);
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
}
