using OneCode.Infrastructure.Agent;

namespace OneCode.Tests;

/// <summary>
/// 工作记忆 store 的延迟建目录契约：MAF <c>FileMemoryProvider</c> 每个 run 都会对会话
/// 工作目录调用 <c>CreateDirectoryAsync</c>，Harness 又给每个会话分配新目录——
/// 急切建根会让磁盘上每会话堆积一个空目录。构造与只读调用都不得触碰磁盘。
/// </summary>
public sealed class FileMemoryLazyRootTests : IDisposable
{
    private readonly string _projectDir;

    public FileMemoryLazyRootTests()
    {
        _projectDir = Path.Combine(
            Path.GetTempPath(), $"onecode-lazy-root-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectDir))
            Directory.Delete(_projectDir, recursive: true);
    }

    private static string RootPathOf(string projectDir) =>
        Path.Combine(projectDir, ".onecode", FileMemoryStorePaths.WorkingMemoryDirName);

    [Fact]
    public async Task ConstructionAndReadOnlyCalls_CreateNoDirectoryOnDisk()
    {
        var store = FileMemoryStorePaths.CreateStore(_projectDir);
        var ct = TestContext.Current.CancellationToken;

        // Harness 在每次 run 都会调用 CreateDirectoryAsync（会话工作目录路径）——
        // 它必须是无操作，否则会话目录在首次写入前就已落盘。
        await store.CreateDirectoryAsync("sessions/20261010", ct);

        var read = await store.ReadAsync("notes.md", ct);
        read.Should().BeNull("根目录尚不存在，读取返回 null");

        Directory.Exists(RootPathOf(_projectDir)).Should().BeFalse(
            "构造 + CreateDirectoryAsync + 只读调用都不得创建任何目录");
    }

    [Fact]
    public async Task FirstWrite_CreatesRootAndFile()
    {
        var store = FileMemoryStorePaths.CreateStore(_projectDir);
        var ct = TestContext.Current.CancellationToken;

        await store.WriteAsync("notes.md", "hello", ct);

        var written = Path.Combine(RootPathOf(_projectDir), "notes.md");
        File.Exists(written).Should().BeTrue("首次写入必须真正落盘（延迟建目录不吞掉写入）");
        (await File.ReadAllTextAsync(written, ct)).Should().Be("hello");

        // 写入后读取走通：同一 store 实例能读回刚写的内容。
        (await store.ReadAsync("notes.md", ct)).Should().Be("hello");
    }
}
