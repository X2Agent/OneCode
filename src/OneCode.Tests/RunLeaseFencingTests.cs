using System.Diagnostics;
using System.Text.Json;

namespace OneCode.Tests;

/// <summary>Cross-process gates for the S-07 run lease and fencing protocol.</summary>
public sealed class RunLeaseFencingTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "onecode-tests",
        "run-lease-fencing",
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
    public async Task ConcurrentAcquire_OnlyOneProcessWins_AndTakeoverIncrementsToken()
    {
        var runId = $"run-{Guid.NewGuid():N}";
        var holderResult = Path.Combine(_root, "holder.json");
        using var holder = StartProbe("lease-hold", runId, holderResult);
        LeaseProbeResult? first = null;
        try
        {
            await WaitForFileAsync(holderResult, TestContext.Current.CancellationToken);
            first = await ReadResultAsync(holderResult, TestContext.Current.CancellationToken);

            var contenderResult = Path.Combine(_root, "contender.json");
            using var contender = StartProbe("lease-try", runId, contenderResult);
            await contender.WaitForExitAsync(TestContext.Current.CancellationToken);
            var contended = await ReadResultAsync(contenderResult, TestContext.Current.CancellationToken);

            first.Acquired.Should().BeTrue();
            first.FencingToken.Should().Be(1);
            contender.ExitCode.Should().Be(2);
            contended.Acquired.Should().BeFalse();
            contended.State.Should().Be("Contended");
        }
        finally
        {
            await File.WriteAllTextAsync(
                holderResult + ".release",
                "release",
                TestContext.Current.CancellationToken);
            await holder.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        holder.ExitCode.Should().Be(0);

        var takeoverResult = Path.Combine(_root, "takeover.json");
        using var takeover = StartProbe("lease-acquire", runId, takeoverResult);
        await takeover.WaitForExitAsync(TestContext.Current.CancellationToken);
        var second = await ReadResultAsync(takeoverResult, TestContext.Current.CancellationToken);

        takeover.ExitCode.Should().Be(0);
        second.Acquired.Should().BeTrue();
        first.Should().NotBeNull();
        second.FencingToken.Should().BeGreaterThan(first!.FencingToken);
    }

    [Fact]
    public async Task StaleHolder_CannotWriteEvidenceAfterTakeover()
    {
        var runId = $"run-{Guid.NewGuid():N}";
        var first = await AcquireAsync(runId, "first");
        var second = await AcquireAsync(runId, "second");

        var staleWrite = await RunTokenOperationAsync("lease-write", runId, first.FencingToken, "stale-write");
        var currentWrite = await RunTokenOperationAsync("lease-write", runId, second.FencingToken, "current-write");

        staleWrite.Process.ExitCode.Should().NotBe(0);
        staleWrite.StandardError.Should().Contain("Stale fencing token");
        currentWrite.Process.ExitCode.Should().Be(0, currentWrite.StandardError);
    }

    [Fact]
    public async Task CompletedRun_IsImmutable_AndCleanupIsIdempotent()
    {
        var runId = $"run-{Guid.NewGuid():N}";
        var lease = await AcquireAsync(runId, "active");
        var checkpointArtifact = ResolveRunArtifact(runId, ".checkpoint.tmp");
        await File.WriteAllTextAsync(checkpointArtifact, "checkpoint", TestContext.Current.CancellationToken);

        var complete = await RunTokenOperationAsync("lease-complete", runId, lease.FencingToken, "complete");
        var duplicateComplete = await RunTokenOperationAsync("lease-complete", runId, lease.FencingToken, "complete-again");
        var cleanup = await RunTokenOperationAsync("lease-cleanup", runId, lease.FencingToken, "cleanup");
        var duplicateCleanup = await RunTokenOperationAsync("lease-cleanup", runId, lease.FencingToken, "cleanup-again");

        complete.Process.ExitCode.Should().Be(0, complete.StandardError);
        duplicateComplete.Process.ExitCode.Should().Be(0, duplicateComplete.StandardError);
        cleanup.Process.ExitCode.Should().Be(0, cleanup.StandardError);
        duplicateCleanup.Process.ExitCode.Should().Be(0, duplicateCleanup.StandardError);
        File.Exists(checkpointArtifact).Should().BeFalse();

        var reacquireResult = Path.Combine(_root, "reacquire-completed.json");
        using var reacquire = StartProbe("lease-acquire", runId, reacquireResult);
        var reacquireError = reacquire.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await reacquire.WaitForExitAsync(TestContext.Current.CancellationToken);
        reacquire.ExitCode.Should().NotBe(0);
        (await reacquireError).Should().Contain("completed and cannot be acquired");
    }

    [Fact]
    public async Task OrphanCheckpoint_DoesNotCreateBusinessRun()
    {
        var runId = $"orphan-{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(
            ResolveRunArtifact(runId, ".checkpoint.tmp"),
            "orphan",
            TestContext.Current.CancellationToken);

        Directory.GetFiles(_root, "*.run.json").Should().BeEmpty();
        File.Exists(ResolveRunArtifact(runId, ".run.json")).Should().BeFalse();
    }

    private async Task<LeaseProbeResult> AcquireAsync(string runId, string name)
    {
        var resultPath = Path.Combine(_root, name + ".json");
        using var process = StartProbe("lease-acquire", runId, resultPath);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        process.ExitCode.Should().Be(0, await error);
        return await ReadResultAsync(resultPath, TestContext.Current.CancellationToken);
    }

    private async Task<TokenOperationResult> RunTokenOperationAsync(
        string mode,
        string runId,
        long token,
        string name)
    {
        var resultPath = Path.Combine(_root, name + ".json");
        await File.WriteAllTextAsync(
            resultPath + ".token",
            token.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TestContext.Current.CancellationToken);
        using var process = StartProbe(mode, runId, resultPath);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new TokenOperationResult(process.ExitCode, await error);
    }

    private Process StartProbe(string mode, string runId, string resultPath)
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
        startInfo.ArgumentList.Add("lease");
        startInfo.ArgumentList.Add(_root);
        startInfo.ArgumentList.Add(runId);
        startInfo.ArgumentList.Add(resultPath);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start run lease probe.");
    }

    private string ResolveRunArtifact(string runId, string suffix)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(runId))).ToLowerInvariant();
        return Path.Combine(_root, key + suffix);
    }

    private static async Task<LeaseProbeResult> ReadResultAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<LeaseProbeResult>(stream, cancellationToken: ct)
            ?? throw new InvalidDataException($"Lease result '{path}' was empty.");
    }

    private static async Task WaitForFileAsync(string path, CancellationToken ct)
    {
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(path))
        {
            if (DateTime.UtcNow >= timeout)
                throw new TimeoutException($"Timed out waiting for '{path}'.");
            await Task.Delay(20, ct);
        }
    }

    private static string ResolveTestAssemblyDll()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "OneCode.Tests.dll");
        if (!File.Exists(candidate))
            throw new FileNotFoundException("The test assembly has not been built.", candidate);
        return candidate;
    }

    private sealed record LeaseProbeResult(bool Acquired, long FencingToken, string State);
    private sealed record TokenOperationResult(int ExitCode, string StandardError)
    {
        public ProcessSnapshot Process => new(ExitCode);
    }
    private sealed record ProcessSnapshot(int ExitCode);
}
