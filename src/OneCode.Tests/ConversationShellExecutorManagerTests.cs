using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Tools;

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
        var result = await _sut.ExecuteAsync(id, _workDir, "echo ok", CommandTimeout, ct: ct);
        result.ExitCode.Should().Be(0);
        var firstExecutor = _sut.TryGet(id);
        firstExecutor.Should().NotBeNull();

        // 再次释放后执行必须重建新实例（复用契约的另一半）。
        await _sut.ReleaseAsync(id);
        _sut.TryGet(id).Should().BeNull("释放后不得残留旧执行器");
        var second = await _sut.ExecuteAsync(id, _workDir, "echo ok", CommandTimeout, ct: ct);
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

        var first = await _sut.ExecuteAsync(id, _workDir, "echo first", CommandTimeout, ct: ct);
        first.ExitCode.Should().Be(0);

        var created = _sut.TryGet(id);
        created.Should().NotBeNull();

        var second = await _sut.ExecuteAsync(id, _workDir, "echo second", CommandTimeout, ct: ct);
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
            _sut.ExecuteAsync(a, _workDir, "echo alpha", CommandTimeout, ct: ct),
            _sut.ExecuteAsync(b, _workDir, "echo beta", CommandTimeout, ct: ct));

        results[0].ExitCode.Should().Be(0);
        results[1].ExitCode.Should().Be(0);
        _sut.TryGet(a).Should().NotBeSameAs(_sut.TryGet(b));
    }

    [Fact]
    public async Task ReleaseAsync_ThenExecute_StartsAFreshSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = new SessionId("conv-release");

        await _sut.ExecuteAsync(id, _workDir, "echo before", CommandTimeout, ct: ct);
        var original = _sut.TryGet(id);

        await _sut.ReleaseAsync(id);
        _sut.TryGet(id).Should().BeNull();

        var after = await _sut.ExecuteAsync(id, _workDir, "echo after", CommandTimeout, ct: ct);
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

        var inFlight = _sut.ExecuteAsync(id, _workDir, "echo racing", CommandTimeout, ct: ct);
        var release = _sut.ReleaseAsync(id);

        var result = await inFlight;
        await release;

        result.ExitCode.Should().Be(0);
        OutputOf(result).Should().Contain("racing");
    }

    /// <summary>
    /// cwd 围栏：命令把持久 shell cwd 切到工作区外（此处直接调 manager，模拟静态校验
    /// 无法覆盖的越界途径，如 cd $VAR）。围栏必须在输出中告警、并把 cwd 拉回工作区根，
    /// 后续命令不得残留越界 cwd。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CwdEscapesWorkspace_RestoresRootAndWarns()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = new SessionId("conv-cwd-fence");
        var outside = Path.Combine(Path.GetTempPath(), $"ShellFenceOutside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            var escape = OperatingSystem.IsWindows()
                ? $"Set-Location -LiteralPath '{outside}'"
                : $"cd '{outside}'";
            var result = await _sut.ExecuteAsync(id, _workDir, escape, CommandTimeout, ct: ct);

            result.ExitCode.Should().Be(0);
            result.Stdout.Should().Contain(
                "[warning] Shell working directory escaped",
                "围栏必须把越界事件显式告知模型与用户");
            result.Stdout.Should().Contain(_workDir, "告警必须说明已恢复到工作区根");

            // 围栏拉回后，后续命令必须在工作区根执行——若围栏失效会残留 outside。
            var followUp = await _sut.ExecuteAsync(
                id, _workDir, OperatingSystem.IsWindows() ? "(Get-Location).Path" : "pwd", CommandTimeout, ct: ct);
            followUp.Stdout.Should().Contain(_workDir, "后续命令的 cwd 必须已被拉回工作区根");
            followUp.Stdout.Should().NotContain(outside);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// cwd 围栏白名单：/add-dir 授权目录是合法 cwd 目标，切过去不得告警、
    /// 且持久语义保留（后续命令仍在授权目录中执行）。
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CwdWithinAdditionalDirectory_KeepsCwdWithoutWarning()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = new SessionId("conv-cwd-allowed");
        var extra = Path.Combine(Path.GetTempPath(), $"ShellFenceExtra_{Guid.NewGuid():N}");
        Directory.CreateDirectory(extra);
        try
        {
            var escape = OperatingSystem.IsWindows()
                ? $"Set-Location -LiteralPath '{extra}'"
                : $"cd '{extra}'";
            var result = await _sut.ExecuteAsync(
                id, _workDir, escape, CommandTimeout, additionalDirectories: [extra], ct: ct);

            result.ExitCode.Should().Be(0);
            result.Stdout.Should().NotContain("[warning]", "授权目录是合法 cwd 目标");

            var followUp = await _sut.ExecuteAsync(
                id, _workDir, OperatingSystem.IsWindows() ? "(Get-Location).Path" : "pwd",
                CommandTimeout, additionalDirectories: [extra], ct: ct);
            followUp.Stdout.Should().Contain(extra, "授权目录内 cd 的持久语义必须保留");
        }
        finally
        {
            try { Directory.Delete(extra, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task InvalidateShellAsync_ReturnsErrorInsteadOfFalseRestoreSuccess()
    {
        var id = new SessionId("conv-cwd-invalidate");
        var executor = new LocalShellExecutor(new LocalShellExecutorOptions
        {
            Mode = ShellMode.Persistent,
            WorkingDirectory = _workDir,
            AcknowledgeUnsafe = true,
        });
        var session = new ConversationShellExecutorManager.SessionShell(executor, _workDir);
        var original = new ShellResult("stdout", "stderr", 0, TimeSpan.Zero, false, false);

        var result = await _sut.InvalidateShellAsync(
            id, session, original, "the shell could not be restored", error: null);

        result.ExitCode.Should().Be(-1);
        result.Stderr.Should().Contain("Persistent shell was closed");
        result.Stderr.Should().Contain("could not be restored");
        result.Stderr.Should().NotContain("restored to");
        session.Released.Should().BeTrue();
    }
}
