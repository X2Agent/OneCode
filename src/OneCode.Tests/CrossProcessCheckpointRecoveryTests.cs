using System.Diagnostics;
using System.Text.Json;

namespace OneCode.Tests;

/// <summary>
/// S-01 integration gates. Each phase runs in a separate process and rebuilds fresh workflow/executor instances.
/// </summary>
public sealed class CrossProcessCheckpointRecoveryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "onecode-tests",
        "maf-cross-process",
        Guid.NewGuid().ToString("N"));

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CustomWorkflow_RestoresExecutorSharedStatePendingMessageAndRequest()
    {
        var (write, resume) = await RunScenarioAsync("custom");

        AssertCommonRecoveryContract(write, resume);
        resume.RequestId.Should().Be(write.RequestId, "pending request identity must survive recovery");
        resume.PortId.Should().Be(write.PortId, "stable request port identity must survive recovery");
        resume.CommandId.Should().Be(write.CommandId, "business command identity must survive recovery");
        resume.CommandId.Should().Be("probe-request-v1");
        resume.ExecutorCount.Should().Be(1, "executor state must be restored instead of reinitialized");
        resume.SharedCount.Should().Be(1, "shared state must be restored instead of reinitialized");
    }

    [Theory]
    [InlineData("resume-wrong-request", "No pending request with ID")]
    [InlineData("resume-wrong-port", "does not match the originating port id")]
    [InlineData("resume-wrong-session", "not found in store")]
    [InlineData("resume-wrong-checkpoint", "not found in store")]
    public async Task CustomWorkflow_InvalidResponseOrCheckpoint_FailsClosed(
        string mode,
        string expectedError)
    {
        var scenarioRoot = Path.Combine(_root, mode);
        var storeDirectory = Path.Combine(scenarioRoot, "store");
        var sessionId = $"s03-{mode}-{Guid.NewGuid():N}";
        var writeResultPath = Path.Combine(scenarioRoot, "write.json");
        var resumeResultPath = Path.Combine(scenarioRoot, "resume.json");
        Directory.CreateDirectory(scenarioRoot);

        var write = await RunProbeAsync(
            "write", "custom", storeDirectory, sessionId, writeResultPath, TestContext.Current.CancellationToken);
        var invalidResume = await RunProbeAsync(
            mode, "custom", storeDirectory, sessionId, resumeResultPath, TestContext.Current.CancellationToken);

        write.ExitCode.Should().Be(0, write.StandardError);
        invalidResume.ExitCode.Should().NotBe(0);
        invalidResume.StandardError.Should().Contain(expectedError);
        File.Exists(resumeResultPath).Should().BeFalse("a rejected resume must not publish a success result");
    }

    [Fact]
    public async Task CustomWorkflow_DuplicateResponse_IsRejectedOrIdempotentWithoutDuplicateOutput()
    {
        var scenarioRoot = Path.Combine(_root, "duplicate-response");
        var storeDirectory = Path.Combine(scenarioRoot, "store");
        var sessionId = $"s03-duplicate-{Guid.NewGuid():N}";
        var writeResultPath = Path.Combine(scenarioRoot, "write.json");
        var resumeResultPath = Path.Combine(scenarioRoot, "resume.json");
        Directory.CreateDirectory(scenarioRoot);

        var write = await RunProbeAsync(
            "write", "custom", storeDirectory, sessionId, writeResultPath, TestContext.Current.CancellationToken);
        var duplicate = await RunProbeAsync(
            "resume-duplicate", "custom", storeDirectory, sessionId, resumeResultPath, TestContext.Current.CancellationToken);

        write.ExitCode.Should().Be(0, write.StandardError);
        if (duplicate.ExitCode == 0)
        {
            var result = await ReadResultAsync(resumeResultPath, TestContext.Current.CancellationToken);
            result.Success.Should().BeTrue();
            result.ExecutorCount.Should().Be(1);
            result.SharedCount.Should().Be(1);
        }
        else
        {
            duplicate.StandardError.Should().Contain("pending request");
        }
    }

    [Fact]
    public async Task FileSideEffect_AfterCrashBeforeCheckpoint_ReplayUsesOperationReceiptOnce()
    {
        var scenarioRoot = Path.Combine(_root, "side-effect-replay");
        var storeDirectory = Path.Combine(scenarioRoot, "store");
        var sessionId = $"s04-side-effect-{Guid.NewGuid():N}";
        var writeResultPath = Path.Combine(scenarioRoot, "write.json");
        var crashResultPath = Path.Combine(scenarioRoot, "crash.json");
        var resumeResultPath = Path.Combine(scenarioRoot, "resume.json");
        Directory.CreateDirectory(scenarioRoot);

        var write = await RunProbeAsync(
            "write", "sideeffect", storeDirectory, sessionId, writeResultPath, TestContext.Current.CancellationToken);
        var crash = await RunProbeAsync(
            "resume-crash", "sideeffect", storeDirectory, sessionId, crashResultPath, TestContext.Current.CancellationToken);
        var resume = await RunProbeAsync(
            "resume", "sideeffect", storeDirectory, sessionId, resumeResultPath, TestContext.Current.CancellationToken);

        write.ExitCode.Should().Be(0, write.StandardError);
        crash.ExitCode.Should().NotBe(0, "the middle process must terminate after the side effect and before MAF reconciliation");
        crash.StandardError.Should().Contain("Simulated crash after side effect");
        resume.ExitCode.Should().Be(0, resume.StandardError);
        var result = await ReadResultAsync(resumeResultPath, TestContext.Current.CancellationToken);
        result.Success.Should().BeTrue();
        result.Output.Should().Contain("\"OperationReplayed\":true");
        (await File.ReadAllTextAsync(
            Path.Combine(scenarioRoot, "side-effect-count.txt"),
            TestContext.Current.CancellationToken)).Should().Be("1");
        Directory.GetFiles(Path.Combine(scenarioRoot, "operation-ledger"), "*.receipt")
            .Should().ContainSingle();
    }

    [Fact]
    public async Task MagenticWorkflow_RestoresPlanReviewRequestAndCompletes()
    {
        var (write, resume) = await RunScenarioAsync("magentic");

        AssertCommonRecoveryContract(write, resume);
        resume.RequestId.Should().Be(write.RequestId, "Magentic plan review request must be replayed");
        resume.PortId.Should().Be(write.PortId, "Magentic plan review port identity must be stable");
        resume.Output.Should().Contain("magentic-final");
    }

    [Theory]
    [InlineData("sequential")]
    [InlineData("groupchat")]
    public async Task OrchestrationCheckpoint_LoadsInNewProcessWithoutTopologyOrSerializerError(string scenario)
    {
        var (write, resume) = await RunScenarioAsync(scenario);

        AssertCommonRecoveryContract(write, resume);
        resume.Output.Should().BeNull(
            "MAF 1.15 loads these reconstructed orchestration checkpoints but does not continue output from the selected intermediate boundary; " +
            "OneCode must restart the enclosing idempotent Task rather than claim exact internal continuation");
    }

    private async Task<(ProbeResult Write, ProbeResult Resume)> RunScenarioAsync(string scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionId = $"s01-{scenario}-{Guid.NewGuid():N}";
        var scenarioRoot = Path.Combine(_root, scenario);
        var storeDirectory = Path.Combine(scenarioRoot, "store");
        var writeResultPath = Path.Combine(scenarioRoot, "write.json");
        var resumeResultPath = Path.Combine(scenarioRoot, "resume.json");
        Directory.CreateDirectory(scenarioRoot);

        var writeProcess = await RunProbeAsync(
            "write", scenario, storeDirectory, sessionId, writeResultPath, ct);
        var resumeProcess = await RunProbeAsync(
            "resume", scenario, storeDirectory, sessionId, resumeResultPath, ct);

        writeProcess.ExitCode.Should().Be(0, writeProcess.StandardError);
        resumeProcess.ExitCode.Should().Be(0, resumeProcess.StandardError);
        return (
            await ReadResultAsync(writeResultPath, ct),
            await ReadResultAsync(resumeResultPath, ct));
    }

    private static void AssertCommonRecoveryContract(ProbeResult write, ProbeResult resume)
    {
        write.Success.Should().BeTrue();
        resume.Success.Should().BeTrue();
        resume.Scenario.Should().Be(write.Scenario);
        resume.ProcessId.Should().NotBe(write.ProcessId, "write and resume must run in independent processes");
        resume.DefinitionHash.Should().Be(write.DefinitionHash);
        resume.SessionId.Should().Be(write.SessionId);
        resume.CheckpointId.Should().Be(write.CheckpointId);
    }

    private static async Task<ProbeProcessResult> RunProbeAsync(
        string mode,
        string scenario,
        string storeDirectory,
        string sessionId,
        string resultPath,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(ResolveTestAssemblyDll());
        startInfo.ArgumentList.Add("--probe");
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add(scenario);
        startInfo.ArgumentList.Add(storeDirectory);
        startInfo.ArgumentList.Add(sessionId);
        startInfo.ArgumentList.Add(resultPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the MAF recovery probe process.");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(ct);
        var standardErrorTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return new ProbeProcessResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);
    }

    private static string ResolveTestAssemblyDll()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "OneCode.Tests.dll");
        if (!File.Exists(candidate))
            throw new FileNotFoundException("The test assembly has not been built.", candidate);
        return candidate;
    }

    private static async Task<ProbeResult> ReadResultAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ProbeResult>(stream, cancellationToken: ct)
            ?? throw new InvalidDataException($"Probe result '{path}' was empty.");
    }

    private sealed record ProbeProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record ProbeResult(
        bool Success,
        string Phase,
        string Scenario,
        int ProcessId,
        string DefinitionHash,
        string SessionId,
        string CheckpointId,
        string? RequestId,
        string? PortId,
        string? CommandId,
        int? ExecutorCount,
        int? SharedCount,
        string? Output);
}
