using OneCode.Infrastructure;
using OneCode.Infrastructure.Abstractions;
using NSubstitute;

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
        var indexSvc = Substitute.For<ICodeIndexService>();
        indexSvc.UpdateFilesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var reloader = new CodeIndexHotReloader(indexSvc) { DebounceMs = 50 };
        reloader.StartWatching(_tmpDir);

        // Create a source file
        var filePath = Path.Combine(_tmpDir, "New.cs");
        await File.WriteAllTextAsync(filePath, "class NewClass {}");

        // Wait for debounce + async processing
        await Task.Delay(300);

        await indexSvc.Received().UpdateFilesAsync(
            Arg.Is<IEnumerable<string>>(list => list.Contains(filePath)),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FileDeletion_TriggersUpdateFilesAsync_WithRemovedList()
    {
        var filePath = Path.Combine(_tmpDir, "Delete.cs");
        await File.WriteAllTextAsync(filePath, "class DeleteMe {}");

        var indexSvc = Substitute.For<ICodeIndexService>();
        indexSvc.UpdateFilesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var reloader = new CodeIndexHotReloader(indexSvc) { DebounceMs = 50 };
        reloader.StartWatching(_tmpDir);

        File.Delete(filePath);
        await Task.Delay(300);

        await indexSvc.Received().UpdateFilesAsync(
            Arg.Any<IEnumerable<string>>(),
            Arg.Is<IEnumerable<string>>(list => list.Contains(filePath)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonSourceFile_DoesNotTriggerUpdate()
    {
        var indexSvc = Substitute.For<ICodeIndexService>();
        indexSvc.UpdateFilesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var reloader = new CodeIndexHotReloader(indexSvc) { DebounceMs = 50 };
        reloader.StartWatching(_tmpDir);

        // Write a non-source file  — should be ignored
        await File.WriteAllTextAsync(Path.Combine(_tmpDir, "readme.txt"), "text");
        await Task.Delay(300);

        await indexSvc.DidNotReceive().UpdateFilesAsync(
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RapidChanges_AreDebounced_IntoSingleCall()
    {
        var callCount = 0;
        var indexSvc = Substitute.For<ICodeIndexService>();
        indexSvc.UpdateFilesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => { Interlocked.Increment(ref callCount); return Task.CompletedTask; });

        // 300ms 窗口足够宽，确保 5 次快速写入的文件系统事件
        // 全部落在同一个 debounce 窗口内，只触发一次 UpdateFilesAsync。
        using var reloader = new CodeIndexHotReloader(indexSvc) { DebounceMs = 300 };
        reloader.StartWatching(_tmpDir);

        // Write multiple files quickly (well within the debounce window)
        for (var i = 0; i < 5; i++)
            await File.WriteAllTextAsync(Path.Combine(_tmpDir, $"File{i}.cs"), $"class C{i} {{}}");

        // 1200ms = 4×debounce window，足够让 debounce 计时器到期并完成异步调用
        await Task.Delay(1200);

        // 严格断言：debounce 必须将 5 次写入合并为恰好 1 次调用。
        // 如果 debounce 失效会产生 5 次调用，如果部分失效会产生 2-4 次。
        // 只有恰好 1 次才说明 debounce 正确工作。
        callCount.Should().Be(1, "5 次快速写入必须被 debounce 合并为 1 次 UpdateFilesAsync 调用");
    }
}
