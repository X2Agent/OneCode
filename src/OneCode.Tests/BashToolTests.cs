using NSubstitute;
using OneCode.App.Tools;
using OneCode.Core.Tools;
using OneCode.Infrastructure;
using OneCode.Core.IO;


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

    private static IProcessRunner CreateRunner(bool hasPwsh = true)
    {
        var runner = Substitute.For<IProcessRunner>();
        runner.CommandExistsAsync("pwsh")
            .Returns(Task.FromResult(hasPwsh));
        return runner;
    }

    private static BashTool CreateTool(IWorkingDirectoryAccessor wd, IProcessRunner? runner = null)
        => new(wd, ssh: null!, shellExecutorManager: null!, sessionManager: null!, runner ?? Substitute.For<IProcessRunner>());

    // Input validation

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task ExecuteAsync_EmptyOrWhitespaceCommand_ReturnsError(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateTool(CreateWd());

        var result = await sut.ExecuteAsync(command, ct: ct);

        result.Content.Should().Be("Error: command cannot be empty");
    }

    [Fact]
    public async Task ExecuteAsync_MissingWorkingDirectory_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var missingDir = Path.Combine(_sandboxDir, "does-not-exist");
        var sut = CreateTool(CreateWd(missingDir));

        var result = await sut.ExecuteAsync("ls", ct: ct);

        result.Content.Should().StartWith("Error: working directory not found:");
        result.Content.Should().Contain(missingDir);
    }

    // Path validation through the tool's pipeline

    [Fact]
    public async Task ExecuteAsync_TraversalPathInCommand_ReturnsPathError()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateTool(CreateWd());

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
        var sut = CreateTool(CreateWd());

        var result = await sut.ExecuteAsync("sed 's/.*/x/' file.txt", ct: ct);

        result.Content.Should().StartWith("Error: sed command contains a potentially destructive pattern");
    }

    [Fact]
    public async Task ExecuteAsync_SedInPlaceWithoutBackup_ReturnsWarningBeforeExecution()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateTool(CreateWd());

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
        var sut = CreateTool(CreateWd());

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
        var sut = CreateTool(CreateWd());

        var result = await sut.ExecuteAsync("echo hello", ct: ct);

        result.Content.Should().Contain("Exit code: 0");
        result.Content.Should().Contain("hello");
        result.Content.Should().StartWith("Command: echo hello");
    }

    [Fact]
    public async Task ExecuteAsync_RealFailingCommand_ReturnsNonZeroExit()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateTool(CreateWd());

        // `false` is a bash builtin that exits with code 1; on Windows the
        // BashTool delegates to PowerShell where `exit 1` achieves the same.
        var result = await sut.ExecuteAsync(
            OperatingSystem.IsWindows() ? "exit 1" : "false",
            ct: ct);

        result.Content.Should().Contain("Exit code: 1");
    }

    // PowerShell dialect (shell="powershell") — migrated from the former PowerShellTool

    [Fact]
    public async Task ExecuteAsync_PowerShellMissingWorkingDirectory_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var missingDir = Path.Combine(_sandboxDir, "does-not-exist");
        var sut = CreateTool(CreateWd(missingDir), CreateRunner());

        var result = await sut.ExecuteAsync("Get-Process", "powershell", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().StartWith("Error: working directory not found:");
        result.Content.Should().Contain(missingDir);
    }

    [Fact]
    public async Task ExecuteAsync_PowerShellTraversalPathInCommand_ReturnsPathError()
    {
        var ct = TestContext.Current.CancellationToken;
        // Get-Content positional path is extracted by PowerShellCommandClassifier;
        // a path that escapes the working directory must be rejected before
        // any process is started.
        var sut = CreateTool(CreateWd(), CreateRunner());

        var result = await sut.ExecuteAsync("Get-Content ../../../outside/secret.txt", "powershell", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().StartWith("Error: command references path outside the working directory");
        result.Content.Should().Contain("../../../outside/secret.txt");
    }

    [Fact]
    public async Task ExecuteAsync_PowerShellAbsolutePathOutsideWorkingDir_ReturnsPathError()
    {
        var ct = TestContext.Current.CancellationToken;
        var outside = Path.Combine(_sandboxDir, "outside");
        Directory.CreateDirectory(outside);
        var sut = CreateTool(CreateWd(), CreateRunner());

        var result = await sut.ExecuteAsync($"Get-Content {outside}/secret.txt", "powershell", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().StartWith("Error: command references path outside the working directory");
    }

    [Fact]
    public async Task ExecuteAsync_PowerShellDestructiveCommand_ReturnsFormattedResult()
    {
        var ct = TestContext.Current.CancellationToken;
        // Remove-Item with a path that exists inside the working dir so we pass
        // path validation but trigger the destructive-command warning path.
        // The destructive warning is prepended to the real process output, so we
        // must run a real command to observe the full pipeline.
        var runner = new ProcessRunner();
        var hasPwsh = await runner.CommandExistsAsync("pwsh");
        if (!OperatingSystem.IsWindows() && !hasPwsh)
        {
            Assert.Skip("PowerShell not available on this Unix host");
        }

        var target = Path.Combine(_projectDir, "victim.txt");
        await File.WriteAllTextAsync(target, "x", ct);
        var sut = CreateTool(CreateWd(), runner);

        var result = await sut.ExecuteAsync($"Remove-Item -Force {target}", "powershell", ct: ct);

        // The destructive warning is prepended to the process output. Either the
        // file was removed (exit 0) or the command was blocked by policy — either
        // way the warning must appear in the result.
        result.Content.Should().Contain("[warning]");
        result.Content.Should().Contain("Command: Remove-Item");
    }

    [Fact]
    public async Task ExecuteAsync_PowerShellRealWriteOutputCommand_ReturnsEchoedText()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new ProcessRunner();
        var hasPwsh = await runner.CommandExistsAsync("pwsh");
        if (!OperatingSystem.IsWindows() && !hasPwsh)
        {
            Assert.Skip("PowerShell not available — skip smoke test on Unix without pwsh");
        }

        var sut = CreateTool(CreateWd(), runner);

        var result = await sut.ExecuteAsync("Write-Output hello", "powershell", ct: ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Exit code: 0");
        result.Content.Should().Contain("hello");
        result.Content.Should().StartWith("Command: Write-Output hello");
    }

    [Fact]
    public async Task ExecuteAsync_PowerShellRealCommandFailingExitCode_ReturnsNonZeroExit()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new ProcessRunner();
        var hasPwsh = await runner.CommandExistsAsync("pwsh");
        if (!OperatingSystem.IsWindows() && !hasPwsh)
        {
            Assert.Skip("PowerShell not available on this Unix host");
        }

        var sut = CreateTool(CreateWd(), runner);

        // 'throw' forces a non-zero exit code and writes to stderr.
        var result = await sut.ExecuteAsync("throw 'boom'", "powershell", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("Exit code: 1");
        result.Content.Should().Contain("boom");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownShellName_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateTool(CreateWd());

        var result = await sut.ExecuteAsync("echo hi", "zsh", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("unsupported shell");
    }
}
