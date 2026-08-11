using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace OneCode.Tests;

/// <summary>Fault-injection gates for the MAF 1.15 file-system checkpoint store.</summary>
public sealed class CheckpointStoreFaultInjectionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "onecode-tests",
        "checkpoint-store-faults",
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
    public async Task DisposeThenReopen_RetainsCheckpointAndOrderedIndex()
    {
        const string sessionId = "dispose-reopen";
        CheckpointInfo first;
        CheckpointInfo second;
        using (var store = CreateStore())
        {
            first = await store.CreateCheckpointAsync(sessionId, Json("{\"value\":1}"), null);
            second = await store.CreateCheckpointAsync(sessionId, Json("{\"value\":2}"), first);
        }

        using var reopened = CreateStore();
        var index = await reopened.RetrieveIndexAsync(sessionId, null);
        var restored = await reopened.RetrieveCheckpointAsync(sessionId, second);

        index.Should().Equal(first, second);
        restored.GetProperty("value").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task CorruptedCheckpointData_FailsClosed()
    {
        const string sessionId = "corrupt-data";
        CheckpointInfo checkpoint;
        using (var store = CreateStore())
            checkpoint = await store.CreateCheckpointAsync(sessionId, Json("{\"value\":1}"), null);

        var dataPath = ResolveDataPath(checkpoint);
        await File.WriteAllTextAsync(dataPath, "{not-json", TestContext.Current.CancellationToken);

        using var reopened = CreateStore();
        var act = async () => await reopened.RetrieveCheckpointAsync(sessionId, checkpoint);
        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task IndexEntryWithoutData_FailsClosed()
    {
        const string sessionId = "missing-data";
        CheckpointInfo checkpoint;
        using (var store = CreateStore())
            checkpoint = await store.CreateCheckpointAsync(sessionId, Json("{\"value\":1}"), null);
        File.Delete(ResolveDataPath(checkpoint));

        using var reopened = CreateStore();
        var index = await reopened.RetrieveIndexAsync(sessionId, null);
        var act = async () => await reopened.RetrieveCheckpointAsync(sessionId, checkpoint);

        index.Should().Contain(checkpoint, "the built-in index can lead the data file after a crash");
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task TruncatedIndex_FailsClosedInIsolatedProcess()
    {
        using (var store = CreateStore())
            _ = await store.CreateCheckpointAsync("truncated-index", Json("{\"value\":1}"), null);
        await File.AppendAllTextAsync(
            Path.Combine(_root, "index.jsonl"),
            "{truncated",
            TestContext.Current.CancellationToken);

        var marker = Path.Combine(_root, "corrupt-open.marker");
        using var process = StartProbe("store-open", marker);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        process.ExitCode.Should().NotBe(0);
        (await error).Should().Contain("Index corrupted");
        File.Exists(marker).Should().BeFalse();
    }

    [Fact]
    public async Task OrphanAndTemporaryFiles_AreIgnoredByIndex()
    {
        const string sessionId = "orphan-files";
        CheckpointInfo checkpoint;
        using (var store = CreateStore())
            checkpoint = await store.CreateCheckpointAsync(sessionId, Json("{\"value\":1}"), null);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "orphan.json"),
            "{\"value\":99}",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "interrupted.tmp"),
            "partial",
            TestContext.Current.CancellationToken);

        using var reopened = CreateStore();
        var index = await reopened.RetrieveIndexAsync(sessionId, null);

        index.Should().ContainSingle().Which.Should().Be(checkpoint);
    }

    [Fact]
    public async Task TwoProcesses_OpenSameDirectory_SecondProcessFailsUntilDispose()
    {
        var marker = Path.Combine(_root, "holder.marker");
        using var holder = StartProbe("store-hold", marker);
        await WaitForFileAsync(marker, TestContext.Current.CancellationToken);

        try
        {
            var contenderMarker = Path.Combine(_root, "contender.marker");
            using var contender = StartProbe("store-open", contenderMarker);
            var contenderError = contender.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await contender.WaitForExitAsync(TestContext.Current.CancellationToken);

            contender.ExitCode.Should().NotBe(0);
            (await contenderError).Should().Contain("already in use by another process");
        }
        finally
        {
            await File.WriteAllTextAsync(marker + ".release", "release", TestContext.Current.CancellationToken);
            await holder.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        holder.ExitCode.Should().Be(0);

        var reopenedMarker = Path.Combine(_root, "reopened.marker");
        using var reopened = StartProbe("store-open", reopenedMarker);
        await reopened.WaitForExitAsync(TestContext.Current.CancellationToken);
        reopened.ExitCode.Should().Be(0);
        File.Exists(reopenedMarker).Should().BeTrue();
    }

    [Fact]
    public async Task ExternalSerialization_AllowsConcurrentCallersWithoutSharingStoreAccess()
    {
        using var store = CreateStore();
        using var gate = new SemaphoreSlim(1, 1);
        var checkpoints = await Task.WhenAll(Enumerable.Range(0, 20).Select(async index =>
        {
            await gate.WaitAsync(TestContext.Current.CancellationToken);
            try
            {
                return await store.CreateCheckpointAsync(
                    "serialized-callers",
                    Json($"{{\"value\":{index}}}"),
                    null);
            }
            finally
            {
                gate.Release();
            }
        }));

        var storedIndex = await store.RetrieveIndexAsync("serialized-callers", null);
        storedIndex.Should().HaveCount(20);
        storedIndex.Should().OnlyHaveUniqueItems();
        checkpoints.Should().OnlyHaveUniqueItems();
    }

    private FileSystemJsonCheckpointStore CreateStore()
        => new(new DirectoryInfo(_root));

    private string ResolveDataPath(CheckpointInfo checkpoint)
    {
        var line = File.ReadLines(Path.Combine(_root, "index.jsonl"))
            .Select(json => JsonDocument.Parse(json))
            .Single(document => document.RootElement.GetProperty("checkpointInfo")
                .GetProperty("checkpointId").GetString() == checkpoint.CheckpointId);
        using (line)
            return Path.Combine(_root, line.RootElement.GetProperty("fileName").GetString()!);
    }

    private static JsonElement Json(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private Process StartProbe(string mode, string markerPath)
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
        startInfo.ArgumentList.Add("store");
        startInfo.ArgumentList.Add(_root);
        startInfo.ArgumentList.Add("store-session");
        startInfo.ArgumentList.Add(markerPath);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start checkpoint store ownership probe.");
    }

    private static string ResolveTestAssemblyDll()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "OneCode.Tests.dll");
        if (!File.Exists(candidate))
            throw new FileNotFoundException("The test assembly has not been built.", candidate);
        return candidate;
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
}
