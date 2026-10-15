using OneCode.Core.IO;
using OneCode.Core.Tools;
using OneCode.App.Tools;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace OneCode.Tests;

/// <summary>
/// 只读 Git 工具：子动作白名单、参数 token 校验、进程结果映射与输出截断。
/// </summary>
public sealed class GitToolTests
{
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly IWorkingDirectoryAccessor _workingDirectory = Substitute.For<IWorkingDirectoryAccessor>();

    private GitTool CreateSut()
    {
        _workingDirectory.WorkingDirectory.Returns(@"E:\repo-worktree");
        return new(_processRunner, _workingDirectory);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("log")]
    [InlineData("diff")]
    [InlineData("show")]
    [InlineData("branch")]
    [InlineData("remote")]
    public async Task ExecuteAsync_WhitelistedAction_RunsGitAndReturnsStdout(string action)
    {
        var ct = TestContext.Current.CancellationToken;
        _processRunner
            .ExecuteWithTimeoutAsync("git", Arg.Any<string[]>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, $"output of {action}", string.Empty));

        var result = await CreateSut().ExecuteAsync(action, ct: ct);

        result.IsError.Should().BeFalse($"'{action}' is in the read-only whitelist");
        result.Content.Should().Be($"output of {action}");
        await _processRunner.Received(1).ExecuteWithTimeoutAsync(
            "git",
            Arg.Any<string[]>(),
            Arg.Any<string?>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("push")]
    [InlineData("commit")]
    [InlineData("reset")]
    [InlineData("RM -RF /")]
    [InlineData("")]
    public async Task ExecuteAsync_ActionOutsideWhitelist_RejectsWithoutStartingProcess(string action)
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await CreateSut().ExecuteAsync(action, ct: ct);

        result.IsError.Should().BeTrue("mutating or unknown actions must never reach the process runner");
        result.Content.Should().Contain("Allowed read-only actions");
        await _processRunner.DidNotReceiveWithAnyArgs().ExecuteWithTimeoutAsync(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_LogAction_BuildsCountCappedArguments()
    {
        var ct = TestContext.Current.CancellationToken;
        var capturedArgs = CaptureArgs();
        StubSuccess();

        var result = await CreateSut().ExecuteAsync("log", count: 500, ct: ct);

        result.IsError.Should().BeFalse();
        capturedArgs.Should().Equal((System.Collections.Generic.IEnumerable<string>)["log", "--oneline", "--decorate=short", "-n", "200"],
            "count must be clamped to the 1..200 window to bound output size");
    }

    [Fact]
    public async Task ExecuteAsync_LogActionWithRef_AppendsRefToArguments()
    {
        var ct = TestContext.Current.CancellationToken;
        var capturedArgs = CaptureArgs();
        StubSuccess();

        var result = await CreateSut().ExecuteAsync("log", refName: "HEAD~3..HEAD", ct: ct);

        result.IsError.Should().BeFalse();
        capturedArgs.Should().Equal("log", "--oneline", "--decorate=short", "-n", "20", "HEAD~3..HEAD");
    }

    [Fact]
    public async Task ExecuteAsync_DiffActionStagedPath_BuildsPathScopedArguments()
    {
        var ct = TestContext.Current.CancellationToken;
        var capturedArgs = CaptureArgs();
        StubSuccess();

        var result = await CreateSut().ExecuteAsync("diff", path: "src/App.cs", staged: true, ct: ct);

        result.IsError.Should().BeFalse();
        capturedArgs.Should().Equal("diff", "--staged", "--", "src/App.cs");
    }

    [Theory]
    [InlineData("-o")]
    [InlineData("--upload-pack")]
    [InlineData("/abs/path")]
    [InlineData("C:\\evil")]
    [InlineData("a\u0000b")]
    public async Task ExecuteAsync_InvalidToken_ShapedArgument_IsRejected(string badToken)
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await CreateSut().ExecuteAsync("log", refName: badToken, ct: ct);

        result.IsError.Should().BeTrue("options-shaped or rooted tokens are an injection surface");
        result.Content.Should().Contain("Invalid ref");
        await _processRunner.DidNotReceiveWithAnyArgs().ExecuteWithTimeoutAsync(Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_BlameWithoutPath_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await CreateSut().ExecuteAsync("blame", ct: ct);

        result.IsError.Should().BeTrue("blame is meaningless without a file path");
        result.Content.Should().Contain("blame requires a file path");
    }

    [Fact]
    public async Task ExecuteAsync_PathTraversalOutsideWorkingDirectory_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await CreateSut().ExecuteAsync("diff", path: "../../outside/secret.txt", ct: ct);

        result.IsError.Should().BeTrue("relative path traversal escaping the workspace must be blocked");
        result.Content.Should().Contain("outside");
        await _processRunner.DidNotReceiveWithAnyArgs().ExecuteWithTimeoutAsync(
            Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RunsInSessionWorkingDirectory_NotStartupCwd()
    {
        var ct = TestContext.Current.CancellationToken;
        _processRunner
            .ExecuteWithTimeoutAsync("git", Arg.Any<string[]>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, "ok", string.Empty));

        var result = await CreateSut().ExecuteAsync("status", ct: ct);

        result.IsError.Should().BeFalse();
        // EnterWorktree / GOAL worktree 隔离后会话工作目录与启动仓库不同，
        // 检查必须落在会话目录内，否则读的是启动仓库的状态。
        await _processRunner.Received(1).ExecuteWithTimeoutAsync(
            "git",
            Arg.Any<string[]>(),
            @"E:\repo-worktree",
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ProcessFailedToStart_ReturnsActionableError()
    {
        var ct = TestContext.Current.CancellationToken;
        _processRunner
            .ExecuteWithTimeoutAsync("git", Arg.Any<string[]>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((ProcessResult?)null);

        var result = await CreateSut().ExecuteAsync("status", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("failed to start git");
    }

    [Fact]
    public async Task ExecuteAsync_Timeout_ReturnsTimeoutError()
    {
        var ct = TestContext.Current.CancellationToken;
        _processRunner
            .ExecuteWithTimeoutAsync("git", Arg.Any<string[]>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true));

        var result = await CreateSut().ExecuteAsync("status", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("timed out");
    }

    [Fact]
    public async Task ExecuteAsync_NonZeroExit_SurfacesExitCodeAndStderr()
    {
        var ct = TestContext.Current.CancellationToken;
        _processRunner
            .ExecuteWithTimeoutAsync("git", Arg.Any<string[]>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(128, string.Empty, "fatal: not a git repository"));

        var result = await CreateSut().ExecuteAsync("status", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("exit 128").And.Contain("fatal: not a git repository");
    }

    [Fact]
    public async Task ExecuteAsync_OversizedOutput_IsTruncatedWithMarker()
    {
        var ct = TestContext.Current.CancellationToken;
        var oversized = new string('x', 25_000);
        _processRunner
            .ExecuteWithTimeoutAsync("git", Arg.Any<string[]>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, oversized, string.Empty));

        var result = await CreateSut().ExecuteAsync("status", ct: ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("truncated",
            "large diffs must not blow up the model context window");
        result.Content.Length.Should().BeLessThan(oversized.Length);
    }

    [Fact]
    public async Task ExecuteAsync_UnexpectedRunnerFailure_ReturnsErrorInsteadOfThrowing()
    {
        var ct = TestContext.Current.CancellationToken;
        _processRunner
            .ExecuteWithTimeoutAsync("git", Arg.Any<string[]>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("process host gone"));

        var result = await CreateSut().ExecuteAsync("status", ct: ct);

        result.IsError.Should().BeTrue("tool failures must be reported to the model, not crash the run");
        result.Content.Should().Contain("Error");
    }

    private List<string> CaptureArgs()
    {
        var captured = new List<string>();
        _processRunner
            .ExecuteWithTimeoutAsync("git", Arg.Do<string[]>(args => captured.AddRange(args)),
                Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, "ok", string.Empty));
        return captured;
    }

    private void StubSuccess() =>
        _processRunner
            .ExecuteWithTimeoutAsync("git", Arg.Any<string[]>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, "ok", string.Empty));
}