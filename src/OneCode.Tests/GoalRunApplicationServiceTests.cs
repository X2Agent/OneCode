using NSubstitute;
using OneCode.App.Services.GoalMode;
using OneCode.Core.Build;
using OneCode.Core.Domain;
using OneCode.Core.Goals;
using OneCode.Infrastructure.Goals;
using System.Text.Json;

namespace OneCode.Tests;

public sealed class GoalRunApplicationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onecode-goal-app-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Begin_CreatesWorkspaceAndPersistsStableDefinition()
    {
        var store = new JsonGoalRunStore(_root);
        var workspace = Substitute.For<IGoalWorkspaceService>();
        var fingerprint = Substitute.For<IWorkspaceFingerprintProvider>();
        fingerprint.ComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("fingerprint-a");
        workspace.PrepareAsync(Arg.Any<GoalRun>(), Arg.Any<CancellationToken>())
            .Returns(call => Workspace(call.ArgAt<GoalRun>(0)));
        var sut = new GoalRunApplicationService(store, workspace, fingerprint);
        var sessionId = SessionId.NewId();

        var run = await sut.BeginAsync(
            sessionId, "goal", _root, "model-a", "prompt-a", "tools-a",
            new JsonSerializerOptions(), TestContext.Current.CancellationToken);

        run.Version.Should().Be(1);
        run.Workspace.Should().NotBeNull();
        // 持久化 hash 与执行期 Compile 校验（同一 options）必须一致，否则 GoalRun 无法执行。
        run.DefinitionHash.Should().Be(
            GoalWorkflowCompiler.ComputeDefinitionHash(run, "model-a", "prompt-a", "tools-a", new JsonSerializerOptions()));
        await workspace.Received(1).PrepareAsync(Arg.Is<GoalRun>(candidate => candidate.Id == run.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Begin_SameSessionIsIdempotentAndRejectsDefinitionDrift()
    {
        var store = new JsonGoalRunStore(_root);
        var workspace = Substitute.For<IGoalWorkspaceService>();
        var fingerprint = Substitute.For<IWorkspaceFingerprintProvider>();
        fingerprint.ComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("fingerprint-a");
        workspace.PrepareAsync(Arg.Any<GoalRun>(), Arg.Any<CancellationToken>())
            .Returns(call => Workspace(call.ArgAt<GoalRun>(0)));
        var sut = new GoalRunApplicationService(store, workspace, fingerprint);
        var sessionId = SessionId.NewId();
        var first = await sut.BeginAsync(
            sessionId, "goal", _root, "model-a", "prompt-a", "tools-a",
            ct: TestContext.Current.CancellationToken);

        var second = await sut.BeginAsync(
            sessionId, "goal", _root, "model-a", "prompt-a", "tools-a",
            ct: TestContext.Current.CancellationToken);
        second.Id.Should().Be(first.Id);
        await workspace.Received(1).PrepareAsync(Arg.Any<GoalRun>(), Arg.Any<CancellationToken>());

        var drift = () => sut.BeginAsync(
            sessionId, "goal", _root, "model-b", "prompt-a", "tools-a",
            ct: TestContext.Current.CancellationToken);
        await drift.Should().ThrowAsync<InvalidOperationException>().WithMessage("*definition changed*");
    }

    [Fact]
    public async Task Begin_SameSessionDifferentGoalFailsClosed()
    {
        var store = new JsonGoalRunStore(_root);
        var workspace = Substitute.For<IGoalWorkspaceService>();
        var fingerprint = Substitute.For<IWorkspaceFingerprintProvider>();
        fingerprint.ComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("fingerprint-a");
        workspace.PrepareAsync(Arg.Any<GoalRun>(), Arg.Any<CancellationToken>())
            .Returns(call => Workspace(call.ArgAt<GoalRun>(0)));
        var sut = new GoalRunApplicationService(store, workspace, fingerprint);
        var sessionId = SessionId.NewId();
        _ = await sut.BeginAsync(sessionId, "first", _root, "m", "p", "t", ct: TestContext.Current.CancellationToken);

        var act = () => sut.BeginAsync(sessionId, "second", _root, "m", "p", "t", ct: TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*different GoalRun*");
    }

    [Fact]
    public async Task Begin_WorkspaceFingerprintDriftFailsClosed()
    {
        var store = new JsonGoalRunStore(_root);
        var workspace = Substitute.For<IGoalWorkspaceService>();
        var fingerprint = Substitute.For<IWorkspaceFingerprintProvider>();
        // First call returns the original fingerprint; subsequent calls simulate workspace drift.
        var callCount = 0;
        fingerprint.ComputeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++callCount == 1 ? "fingerprint-original" : "fingerprint-drifted");
        workspace.PrepareAsync(Arg.Any<GoalRun>(), Arg.Any<CancellationToken>())
            .Returns(call => Workspace(call.ArgAt<GoalRun>(0)));
        var sut = new GoalRunApplicationService(store, workspace, fingerprint);
        var sessionId = SessionId.NewId();
        _ = await sut.BeginAsync(sessionId, "goal", _root, "model-a", "prompt-a", "tools-a", ct: TestContext.Current.CancellationToken);

        // Second call with drifted workspace fingerprint must fail-closed.
        var act = () => sut.BeginAsync(sessionId, "goal", _root, "model-a", "prompt-a", "tools-a", ct: TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Workspace fingerprint drift*")
            .WithMessage("*fingerprint-original*")
            .WithMessage("*fingerprint-drifted*");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
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
