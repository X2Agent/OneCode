using OneCode.Infrastructure;

using OneCode.Core.Lsp;
using NSubstitute;
using OneCode.Tests.TestSupport;

namespace OneCode.Tests;

public sealed class CodeIndexHotReloaderTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(
        Path.GetTempPath(), "HotReload_" + Guid.NewGuid().ToString("N")[..8]);

    public CodeIndexHotReloaderTests() => Directory.CreateDirectory(_tmpDir);
    public void Dispose() { try { Directory.Delete(_tmpDir, recursive: true); } catch { } }

    [Fact]
    public async Task FileCreation_TriggersUpdateFilesAsync_WithChangedList()
    {
        var created = new List<string>();
        var indexSvc = CreateIndexService(created);

        using var reloader = new CodeIndexHotReloader(indexSvc) { DebounceMs = 50 };
        reloader.StartWatching(_tmpDir);

        var filePath = Path.Combine(_tmpDir, "New.cs");
        await File.WriteAllTextAsync(filePath, "class NewClass {}");

        await Eventually.TrueAsync(
            () => created.Contains(filePath),
            because: "hot-reload must push the created file to the index service",
            ct: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FileDeletion_TriggersUpdateFilesAsync_WithRemovedList()
    {
        var filePath = Path.Combine(_tmpDir, "Delete.cs");
        await File.WriteAllTextAsync(filePath, "class DeleteMe {}");

        var removed = new List<string>();
        var indexSvc = CreateIndexService(removedList: removed);

        using var reloader = new CodeIndexHotReloader(indexSvc) { DebounceMs = 50 };
        reloader.StartWatching(_tmpDir);

        File.Delete(filePath);

        await Eventually.TrueAsync(
            () => removed.Contains(filePath),
            because: "hot-reload must push the deleted file to the index service",
            ct: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NonSourceFile_DoesNotTriggerUpdate()
    {
        var created = new List<string>();
        var indexSvc = CreateIndexService(created);

        using var reloader = new CodeIndexHotReloader(indexSvc) { DebounceMs = 50 };
        reloader.StartWatching(_tmpDir);

        // Write a non-source file — should be ignored
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "readme.txt"), "text");

        // 观察窗口须长于 debounce 窗口，让"误报"有充分机会出现；随后断言零调用。
        await Task.Delay(300);

        created.Should().NotContain(p => p.EndsWith(".txt", StringComparison.Ordinal),
            "non-source files must be ignored by the hot reloader");
        _ = indexSvc.DidNotReceive().UpdateFilesAsync(
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RapidChanges_AreDebounced_IntoSingleCall()
    {
        var callCount = 0;
        var indexSvc = CreateIndexService();
        indexSvc.UpdateFilesAsync(
            Arg.Do<IEnumerable<string>>(_ => Interlocked.Increment(ref callCount)),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var reloader = new CodeIndexHotReloader(indexSvc) { DebounceMs = 100 };
        reloader.StartWatching(_tmpDir);

        // Write multiple files quickly (well within the debounce window)
        for (var i = 0; i < 5; i++)
            await File.WriteAllTextAsync(Path.Combine(_tmpDir, $"File{i}.cs"), $"class C{i} {{}}");

        // 轮询等首次调用（早退），再静默 5×debounce 窗口观察无第二次调用——
        // 若 debounce 失效（每次事件直触发）会观察到 5 次，部分失效会观察到 2-4 次。
        await Eventually.TrueAsync(
            () => Volatile.Read(ref callCount) >= 1,
            because: "debounce must eventually fire once",
            ct: TestContext.Current.CancellationToken);
        await Task.Delay(500);

        Volatile.Read(ref callCount).Should().Be(1, "5 次快速写入必须被 debounce 合并为 1 次 UpdateFilesAsync 调用");
    }

    /// <summary>
    /// 构造 substitute 并把变更/删除列表经 Arg.Do 捕获到调用方集合，供轮询断言。
    /// </summary>
    private static ICodeIndexService CreateIndexService(
        List<string>? createdList = null,
        List<string>? removedList = null)
    {
        var indexSvc = Substitute.For<ICodeIndexService>();
        indexSvc.UpdateFilesAsync(
                Arg.Do<IEnumerable<string>>(list => createdList?.AddRange(list)),
                Arg.Do<IEnumerable<string>>(list => removedList?.AddRange(list)),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return indexSvc;
    }
}
