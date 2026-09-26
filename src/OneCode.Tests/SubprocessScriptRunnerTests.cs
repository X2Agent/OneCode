using System.Reflection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OneCode.App.Skills;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="SubprocessScriptRunner"/> — covers concurrent
/// stdout/stderr reading (deadlock prevention) and stdout capture.
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
        var root = Path.GetDirectoryName(fullPath)!;
        // MAF 1.21: AgentFileSkillPathScope ctor became public (was NonPublic in 1.19).
        // Match both access levels so future accessibility flips do not break this lookup.
        var ctorFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var scopeType = typeof(AgentFileSkillScript)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .First(parameterType => parameterType.Name == "AgentFileSkillPathScope");
        var scope = scopeType.GetConstructors(ctorFlags)
            .Single(constructor => constructor.GetParameters().Length == 2)
            .Invoke([root, root]);
        var scriptConstructor = typeof(AgentFileSkillScript)
            .GetConstructors(ctorFlags)
            .Single(constructor => constructor.GetParameters().Length == 4);
        return (AgentFileSkillScript)scriptConstructor.Invoke(["test", fullPath, scope, NoopRunner]);
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

        // If we get here, no deadlock occurred。stdout 被 MaxOutputChars(30000) 截断并带标记，
        // 截断前的前缀必须完整连续、且不混入 stderr。
        var stdout = (string)result!;
        stdout.Should().NotContain("stderr_line", "stderr 不得混入 stdout 流");
        stdout.Should().EndWith("… [output truncated]", "超过 MaxOutputChars 的输出必须以截断标记结尾");
        // 剥离截断标记；30000 字符按字符切，可能把最后一行切半，故丢弃末行后校验剩余前缀连续。
        var body = stdout[..^"… [output truncated]".Length].TrimEnd('\r', '\n');
        var stdoutLines = body.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        stdoutLines = stdoutLines[..^1];
        stdoutLines.Should().HaveCountGreaterThan(1000, "截断前仍应保留大量 stdout 行");
        stdoutLines[0].Should().Be("stdout_line_1");
        stdoutLines[^1].Should().Be($"stdout_line_{stdoutLines.Length}",
            "截断前输出必须从 1 起连续编号，说明流式读取没有丢行");
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
