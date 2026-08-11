using NSubstitute;
using OneCode.App.Tools;
using OneCode.Core.Tools;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="BashTool"/> — covers input validation,
/// working-directory checks, referenced-path validation, the sed-command
/// safety guard (<see cref="BashTool.IsSedDangerous"/>), and a smoke test
/// that runs a real shell command on the current platform.
/// </summary>
public sealed class BashToolTests : IDisposable
{
    private readonly string _sandboxDir;
    private readonly string _projectDir;

    public BashToolTests()
    {
        _sandboxDir = Path.Combine(Path.GetTempPath(), $"BashToolTests_{Guid.NewGuid():N}");
        _projectDir = Path.Combine(_sandboxDir, "project");
        Directory.CreateDirectory(_projectDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandboxDir, recursive: true); } catch { /* best effort */ }
    }

    private IWorkingDirectoryAccessor CreateWd(string? workingDir = null)
    {
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(workingDir ?? _projectDir);
        return wd;
    }

    // Input validation

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task ExecuteAsync_EmptyOrWhitespaceCommand_ReturnsError(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new BashTool(CreateWd(), ssh: null!, shellExecutorManager: null!, sessionManager: null!);

        var result = await sut.ExecuteAsync(command, ct: ct);

        result.Content.Should().Be("Error: command cannot be empty");
    }

    [Fact]
    public async Task ExecuteAsync_MissingWorkingDirectory_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var missingDir = Path.Combine(_sandboxDir, "does-not-exist");
        var sut = new BashTool(CreateWd(missingDir), ssh: null!, shellExecutorManager: null!, sessionManager: null!);

        var result = await sut.ExecuteAsync("ls", ct: ct);

        result.Content.Should().StartWith("Error: working directory not found:");
        result.Content.Should().Contain(missingDir);
    }

    // Path validation through the tool's pipeline

    [Fact]
    public async Task ExecuteAsync_TraversalPathInCommand_ReturnsPathError()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new BashTool(CreateWd(), ssh: null!, shellExecutorManager: null!, sessionManager: null!);

        var result = await sut.ExecuteAsync("cat ../../../outside/secret.txt", ct: ct);

        result.Content.Should().StartWith("Error: command references path outside the working directory");
    }

    // sed safety guard (public static API)

    [Theory]
    [InlineData("sed 's/.*/x/' file.txt")]
    [InlineData("sed '/^/d' file.txt")]
    [InlineData("sed '/./d' file.txt")]
    [InlineData("sed 's/^.*$//' file.txt")]
    [InlineData("sed 's|.*||' file.txt")]
    [InlineData("sed -i 's/a/b/' file.txt")]
    public void IsSedDangerous_DestructiveOrInPlaceSed_ReturnsTrue(string command)
    {
        BashTool.IsSedDangerous(command).Should().BeTrue();
    }

    [Theory]
    [InlineData("sed 's/a/b/' file.txt")]
    [InlineData("sed -i.bak 's/a/b/' file.txt")]
    [InlineData("sed -n '1,5p' file.txt")]
    [InlineData("echo hello")]
    [InlineData("grep pattern file.txt")]
    [InlineData("")]
    public void IsSedDangerous_SafeSedOrNoSed_ReturnsFalse(string command)
    {
        BashTool.IsSedDangerous(command).Should().BeFalse();
    }

    // sed guard integrated into ExecuteAsync

    [Fact]
    public async Task ExecuteAsync_DestructiveSedPattern_ReturnsErrorBeforeExecution()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new BashTool(CreateWd(), ssh: null!, shellExecutorManager: null!, sessionManager: null!);

        var result = await sut.ExecuteAsync("sed 's/.*/x/' file.txt", ct: ct);

        result.Content.Should().StartWith("Error: sed command contains a potentially destructive pattern");
    }

    [Fact]
    public async Task ExecuteAsync_SedInPlaceWithoutBackup_ReturnsWarningBeforeExecution()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new BashTool(CreateWd(), ssh: null!, shellExecutorManager: null!, sessionManager: null!);

        var result = await sut.ExecuteAsync("sed -i 's/a/b/' file.txt", ct: ct);

        result.Content.Should().StartWith("Warning: sed -i without backup suffix is destructive");
        result.Content.Should().Contain("-i.bak");
    }

    [Fact]
    public async Task ExecuteAsync_SedInPlaceWithBackup_PassesSedGuard()
    {
        var ct = TestContext.Current.CancellationToken;
        // sed -i.bak passes the sed guard; with no real file it will fail at the
        // process level, but the error must NOT be a sed-guard rejection.
        var sut = new BashTool(CreateWd(), ssh: null!, shellExecutorManager: null!, sessionManager: null!);

        var result = await sut.ExecuteAsync("sed -i.bak 's/a/b/' nonexistent.txt", ct: ct);

        result.Content.Should().NotStartWith("Error: sed command contains");
        result.Content.Should().NotStartWith("Warning: sed -i without backup");
        // The process did run (and failed), so we get a formatted result with an exit code.
        result.Content.Should().Contain("Command: sed -i.bak");
        result.Content.Should().Contain("Exit code:");
    }

    // Smoke test: real shell execution

    [Fact]
    public async Task ExecuteAsync_RealEchoCommand_ReturnsEchoedText()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new BashTool(CreateWd(), ssh: null!, shellExecutorManager: null!, sessionManager: null!);

        var result = await sut.ExecuteAsync("echo hello", ct: ct);

        result.Content.Should().Contain("Exit code: 0");
        result.Content.Should().Contain("hello");
        result.Content.Should().StartWith("Command: echo hello");
    }

    [Fact]
    public async Task ExecuteAsync_RealFailingCommand_ReturnsNonZeroExit()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new BashTool(CreateWd(), ssh: null!, shellExecutorManager: null!, sessionManager: null!);

        // `false` is a bash builtin that exits with code 1; on Windows the
        // BashTool delegates to PowerShell where `exit 1` achieves the same.
        var result = await sut.ExecuteAsync(
            OperatingSystem.IsWindows() ? "exit 1" : "false",
            ct: ct);

        result.Content.Should().Contain("Exit code: 1");
    }
}
