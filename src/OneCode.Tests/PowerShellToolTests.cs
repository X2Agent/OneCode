using NSubstitute;
using OneCode.App.Tools;
using OneCode.Core.Tools;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Abstractions;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="PowerShellTool"/> — covers input validation,
/// working-directory checks, referenced-path validation, destructive-command
/// warning surfacing, and a smoke test that runs a real PowerShell command.
/// Path traversal is exercised through the tool's own validation pipeline
/// (PowerShellCommandClassifier + ShellExecutionHelper) rather than re-tested
/// at the classifier level.
/// </summary>
public sealed class PowerShellToolTests : IDisposable
{
    private readonly string _sandboxDir;
    private readonly string _projectDir;

    public PowerShellToolTests()
    {
        _sandboxDir = Path.Combine(Path.GetTempPath(), $"PowerShellToolTests_{Guid.NewGuid():N}");
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

    // Input validation

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task ExecuteAsync_EmptyOrWhitespaceCommand_ReturnsError(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new PowerShellTool(CreateRunner(), CreateWd(), ssh: null!);

        var result = await sut.ExecuteAsync(command, ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Be("Error: command cannot be empty");
    }

    [Fact]
    public async Task ExecuteAsync_MissingWorkingDirectory_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var missingDir = Path.Combine(_sandboxDir, "does-not-exist");
        var sut = new PowerShellTool(CreateRunner(), CreateWd(missingDir), ssh: null!);

        var result = await sut.ExecuteAsync("Get-Process", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().StartWith("Error: working directory not found:");
        result.Content.Should().Contain(missingDir);
    }

    // Path validation through the tool's pipeline

    [Fact]
    public async Task ExecuteAsync_TraversalPathInCommand_ReturnsPathError()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new PowerShellTool(CreateRunner(), CreateWd(), ssh: null!);

        // Get-Content positional path is extracted by PowerShellCommandClassifier;
        // a path that escapes the working directory must be rejected before
        // any process is started.
        var result = await sut.ExecuteAsync("Get-Content ../../../outside/secret.txt", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().StartWith("Error: command references path outside the working directory");
        result.Content.Should().Contain("../../../outside/secret.txt");
    }

    [Fact]
    public async Task ExecuteAsync_AbsolutePathOutsideWorkingDir_ReturnsPathError()
    {
        var ct = TestContext.Current.CancellationToken;
        var outside = Path.Combine(_sandboxDir, "outside");
        Directory.CreateDirectory(outside);
        var sut = new PowerShellTool(CreateRunner(), CreateWd(), ssh: null!);

        var result = await sut.ExecuteAsync($"Get-Content {outside}/secret.txt", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().StartWith("Error: command references path outside the working directory");
    }

    // Destructive-command warning is surfaced in output

    [Fact]
    public async Task ExecuteAsync_DestructiveCommand_ReturnsFormattedError()
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
        var sut = new PowerShellTool(runner, CreateWd(), ssh: null!);

        var result = await sut.ExecuteAsync($"Remove-Item -Force {target}", ct: ct);

        // The destructive warning is prepended to the process output. Either the
        // file was removed (exit 0) or the command was blocked by policy — either
        // way the warning must appear in the result.
        result.Content.Should().Contain("[warning]");
        result.Content.Should().Contain("Command: Remove-Item");
    }

    // Smoke test: real PowerShell execution

    [Fact]
    public async Task ExecuteAsync_RealWriteOutputCommand_ReturnsEchoedText()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new ProcessRunner();
        var hasPwsh = await runner.CommandExistsAsync("pwsh");
        if (!OperatingSystem.IsWindows() && !hasPwsh)
        {
            Assert.Skip("PowerShell not available — skip smoke test on Unix without pwsh");
        }

        var sut = new PowerShellTool(runner, CreateWd(), ssh: null!);

        var result = await sut.ExecuteAsync("Write-Output hello", ct: ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Exit code: 0");
        result.Content.Should().Contain("hello");
        result.Content.Should().StartWith("Command: Write-Output hello");
    }

    [Fact]
    public async Task ExecuteAsync_RealCommandFailingExitCode_ReturnsNonZeroExit()
    {
        var ct = TestContext.Current.CancellationToken;
        var runner = new ProcessRunner();
        var hasPwsh = await runner.CommandExistsAsync("pwsh");
        if (!OperatingSystem.IsWindows() && !hasPwsh)
        {
            Assert.Skip("PowerShell not available on this Unix host");
        }

        var sut = new PowerShellTool(runner, CreateWd(), ssh: null!);

        // 'throw' forces a non-zero exit code and writes to stderr.
        var result = await sut.ExecuteAsync("throw 'boom'", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("Exit code: 1");
        result.Content.Should().Contain("boom");
    }
}
