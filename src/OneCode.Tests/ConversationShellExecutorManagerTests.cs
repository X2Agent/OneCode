using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Tools;
using OneCode.Core.Domain;

namespace OneCode.Tests;

/// <summary>
/// 生命周期契约测试：<see cref="ConversationShellExecutorManager"/> 每会话复用单个执行器，
/// 释放后重新建立，且会话关闭与在途命令竞争时不得损坏命令结果。
///
/// 注意：同一执行器上命令的串行化由 MAF 的 <c>LocalShellExecutor</c> 内部保证
/// （实测同一会话 4×sleep 1 串行耗时约 4s，不同会话并行约 1s），
/// 故此处不重复断言该行为。
/// </summary>
public sealed class ConversationShellExecutorManagerTests : IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    private readonly string _workDir;
    private readonly ConversationShellExecutorManager _sut;

    public ConversationShellExecutorManagerTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ShellExecutorTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _sut = new ConversationShellExecutorManager(
            NullLogger<ConversationShellExecutorManager>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _sut.DisposeAsync();
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private static string OutputOf(ShellResult result) =>
        ShellExecutionHelper.BuildOutput(result.Stdout, result.Stderr);

    [Fact]
    public void TryGet_UnknownConversation_ReturnsNull()
    {
        _sut.TryGet(new SessionId("unknown-conversation")).Should().BeNull();
    }

    [Fact]
    public async Task ReleaseAsync_UnknownConversation_LeavesManagerUsable()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = new SessionId("unknown-conversation");

        await _sut.ReleaseAsync(id);

        // Releasing an unknown conversation must be a no-op that leaves the manager usable.
        _sut.TryGet(id).Should().BeNull();
        var result = await _sut.ExecuteAsync(id, _workDir, "echo ok", CommandTimeout, ct);
        result.ExitCode.Should().Be(0);
        var firstExecutor = _sut.TryGet(id);
        firstExecutor.Should().NotBeNull();

        // 再次释放后执行必须重建新实例（复用契约的另一半）。
        await _sut.ReleaseAsync(id);
        _sut.TryGet(id).Should().BeNull("释放后不得残留旧执行器");
        var second = await _sut.ExecuteAsync(id, _workDir, "echo ok", CommandTimeout, ct);
        second.ExitCode.Should().Be(0);
        var rebuilt = _sut.TryGet(id);
        rebuilt.Should().NotBeNull().And.NotBeSameAs(firstExecutor, "释放后必须重建全新执行器");
    }

    [Fact]
    public async Task ExecuteAsync_SameConversation_ReusesSingleExecutor()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = new SessionId("conv-reuse");

        _sut.TryGet(id).Should().BeNull();

        var first = await _sut.ExecuteAsync(id, _workDir, "echo first", CommandTimeout, ct);
        first.ExitCode.Should().Be(0);

        var created = _sut.TryGet(id);
        created.Should().NotBeNull();

        var second = await _sut.ExecuteAsync(id, _workDir, "echo second", CommandTimeout, ct);
        second.ExitCode.Should().Be(0);
        _sut.TryGet(id).Should().BeSameAs(created);
    }

    [Fact]
    public async Task ExecuteAsync_DifferentConversations_UseIndependentExecutors()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = new SessionId("conv-independent-a");
        var b = new SessionId("conv-independent-b");

        var results = await Task.WhenAll(
            _sut.ExecuteAsync(a, _workDir, "echo alpha", CommandTimeout, ct),
            _sut.ExecuteAsync(b, _workDir, "echo beta", CommandTimeout, ct));

        results[0].ExitCode.Should().Be(0);
        results[1].ExitCode.Should().Be(0);
        _sut.TryGet(a).Should().NotBeSameAs(_sut.TryGet(b));
    }

    [Fact]
    public async Task ReleaseAsync_ThenExecute_StartsAFreshSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = new SessionId("conv-release");

        await _sut.ExecuteAsync(id, _workDir, "echo before", CommandTimeout, ct);
        var original = _sut.TryGet(id);

        await _sut.ReleaseAsync(id);
        _sut.TryGet(id).Should().BeNull();

        var after = await _sut.ExecuteAsync(id, _workDir, "echo after", CommandTimeout, ct);
        after.ExitCode.Should().Be(0);
        _sut.TryGet(id).Should().NotBeSameAs(original);
    }

    /// <summary>
    /// 会话关闭与在途命令竞争：命令必须正常返回，不能命中"用已释放执行器"的路径。
    /// </summary>
    [Fact]
    public async Task ReleaseAsync_RacingAnInFlightCommand_DoesNotCorruptTheCommand()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = new SessionId("conv-release-race");

        var inFlight = _sut.ExecuteAsync(id, _workDir, "echo racing", CommandTimeout, ct);
        var release = _sut.ReleaseAsync(id);

        var result = await inFlight;
        await release;

        result.ExitCode.Should().Be(0);
        OutputOf(result).Should().Contain("racing");
    }
}
