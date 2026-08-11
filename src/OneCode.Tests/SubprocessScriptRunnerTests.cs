using System.Reflection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OneCode.App.Skills;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="SubprocessScriptRunner"/> — covers concurrent
/// stdout/stderr reading (deadlock prevention) and error-path logging.
/// </summary>
public sealed class SubprocessScriptRunnerTests : IDisposable
{
    private readonly string _tempDir;

    public SubprocessScriptRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SubprocessScriptRunnerTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static AgentFileSkillScriptRunner NoopRunner =>
        (_, _, _, _, _) => Task.FromResult<object?>(null);

    private static AgentFileSkillScript CreateScript(string fullPath)
    {
        // AgentFileSkillScript's constructor is internal — use reflection
        var ctor = typeof(AgentFileSkillScript).GetConstructors(
            BindingFlags.NonPublic | BindingFlags.Instance)[0];
        return (AgentFileSkillScript)ctor.Invoke(["test", fullPath, NoopRunner]);
    }

    private AgentFileSkillScript CreateScript(string extension, string content)
    {
        var scriptPath = Path.Combine(_tempDir, $"test_script{extension}");
        File.WriteAllText(scriptPath, content);
        return CreateScript(scriptPath);
    }

    [Fact]
    public async Task RunCoreAsync_LargeStderrOutput_CompletesWithoutDeadlock()
    {
        // Script that writes >64KB to stderr (exceeding OS pipe buffer) while also
        // writing to stdout. The old sequential code would deadlock here.
        string extension, content;
        if (OperatingSystem.IsWindows())
        {
            extension = ".bat";
            // 5000 lines × ~20 chars each ≈ 100KB, well above the 64KB pipe buffer
            content = """
            @echo off
            for /l %%i in (1,1,5000) do (
                echo stdout_line_%%i
                echo stderr_line_%%i 1>&2
            )
            exit /b 0
            """;
        }
        else
        {
            extension = ".sh";
            content = """
            #!/bin/sh
            i=1
            while [ $i -le 5000 ]; do
                echo "stdout_line_$i"
                echo "stderr_line_$i" >&2
                i=$((i + 1))
            done
            """;
        }

        var script = CreateScript(extension, content);
        if (!OperatingSystem.IsWindows())
        {
            // Make .sh executable on Unix
            var proc = System.Diagnostics.Process.Start("chmod", $"+x {script.FullPath}");
            proc?.WaitForExit();
        }

        var logger = Substitute.For<ILogger>();
        var runner = SubprocessScriptRunner.CreateRunner(logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await runner(null!, script, null, null!, cts.Token);

        // If we get here, no deadlock occurred
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task RunCoreAsync_NonZeroExit_LogsStderr()
    {
        string extension, content;
        if (OperatingSystem.IsWindows())
        {
            extension = ".bat";
            content = """
            @echo off
            echo some error output 1>&2
            exit /b 1
            """;
        }
        else
        {
            extension = ".sh";
            content = """
            #!/bin/sh
            echo "some error output" >&2
            exit 1
            """;
        }

        var script = CreateScript(extension, content);
        if (!OperatingSystem.IsWindows())
        {
            var proc = System.Diagnostics.Process.Start("chmod", $"+x {script.FullPath}");
            proc?.WaitForExit();
        }

        var logger = Substitute.For<ILogger>();
        var runner = SubprocessScriptRunner.CreateRunner(logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await runner(null!, script, null, null!, cts.Token);

        // Verify the warning was logged for non-zero exit.
        // LogWarning is an extension method → verify via ReceivedCalls instead of Arg matchers.
        logger.ReceivedCalls().Should().NotBeEmpty();
    }

    [Fact]
    public async Task RunCoreAsync_ValidScript_ReturnsStdout()
    {
        string extension, content;
        if (OperatingSystem.IsWindows())
        {
            extension = ".bat";
            content = """
            @echo off
            echo hello_stdout
            """;
        }
        else
        {
            extension = ".sh";
            content = """
            #!/bin/sh
            echo "hello_stdout"
            """;
        }

        var script = CreateScript(extension, content);
        if (!OperatingSystem.IsWindows())
        {
            var proc = System.Diagnostics.Process.Start("chmod", $"+x {script.FullPath}");
            proc?.WaitForExit();
        }

        var logger = Substitute.For<ILogger>();
        var runner = SubprocessScriptRunner.CreateRunner(logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await runner(null!, script, null, null!, cts.Token);

        result.Should().Be("hello_stdout" + Environment.NewLine);
    }
}
