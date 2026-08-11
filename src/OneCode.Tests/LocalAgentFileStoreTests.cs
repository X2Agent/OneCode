using Microsoft.Agents.AI;
using NSubstitute;
using OneCode.Core.Tools;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Abstractions;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="LocalAgentFileStore"/> — verifies file CRUD operations,
/// path traversal protection, and IFileSystem implementation.
/// </summary>
public sealed class LocalAgentFileStoreTests : IDisposable
{
    private readonly string _sandboxDir;
    private readonly string _projectDir;
    private readonly string _outsideDir;

    public LocalAgentFileStoreTests()
    {
        _sandboxDir = Path.Combine(Path.GetTempPath(), $"LocalAgentFileStoreTests_{Guid.NewGuid():N}");
        _projectDir = Path.Combine(_sandboxDir, "project");
        _outsideDir = Path.Combine(_sandboxDir, "outside");
        Directory.CreateDirectory(_projectDir);
        Directory.CreateDirectory(_outsideDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandboxDir, recursive: true); } catch { /* best effort */ }
    }

    private IWorkingDirectoryAccessor CreateWd()
    {
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(_projectDir);
        return wd;
    }

    // AgentFileStore methods

    [Fact]
    public async Task ReadAsync_ExistingFile_ReturnsContent()
    {
        var path = Path.Combine(_projectDir, "test.txt");
        File.WriteAllText(path, "hello world");
        var store = new LocalAgentFileStore(CreateWd());

        var content = await store.ReadAsync("test.txt");

        content.Should().Be("hello world");
    }

    [Fact]
    public async Task ReadAsync_NonExistentFile_ReturnsNull()
    {
        var store = new LocalAgentFileStore(CreateWd());

        var content = await store.ReadAsync("nonexistent.txt");

        content.Should().BeNull();
    }

    [Fact]
    public async Task WriteAsync_CreatesFileAndParentDirs()
    {
        var store = new LocalAgentFileStore(CreateWd());

        await store.WriteAsync("sub/dir/file.txt", "content");

        File.Exists(Path.Combine(_projectDir, "sub", "dir", "file.txt")).Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(_projectDir, "sub", "dir", "file.txt")))
            .Should().Be("content");
    }

    [Fact]
    public async Task DeleteAsync_ExistingFile_ReturnsTrueAndDeletes()
    {
        var path = Path.Combine(_projectDir, "delete-me.txt");
        File.WriteAllText(path, "data");
        var store = new LocalAgentFileStore(CreateWd());

        var result = await store.DeleteAsync("delete-me.txt");

        result.Should().BeTrue();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_NonExistentFile_ReturnsFalse()
    {
        var store = new LocalAgentFileStore(CreateWd());

        var result = await store.DeleteAsync("nonexistent.txt");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task FileExistsAsync_ExistingFile_ReturnsTrue()
    {
        File.WriteAllText(Path.Combine(_projectDir, "exists.txt"), "data");
        var store = new LocalAgentFileStore(CreateWd());

        var result = await store.FileExistsAsync("exists.txt");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ListChildrenAsync_ReturnsDirectoriesFirst()
    {
        Directory.CreateDirectory(Path.Combine(_projectDir, "subdir"));
        File.WriteAllText(Path.Combine(_projectDir, "file1.txt"), "a");
        File.WriteAllText(Path.Combine(_projectDir, "file2.txt"), "b");
        var store = new LocalAgentFileStore(CreateWd());

        var entries = await store.ListChildrenAsync("");

        entries.Should().HaveCount(3);
        entries[0].Type.Should().Be(FileStoreEntry.Directory);
        entries[0].Name.Should().Be("subdir");
        entries[1].Type.Should().Be(FileStoreEntry.File);
        entries[2].Type.Should().Be(FileStoreEntry.File);
    }

    [Fact]
    public async Task SearchAsync_FindsMatchingContent()
    {
        File.WriteAllText(Path.Combine(_projectDir, "a.txt"), "hello world\nerror here");
        File.WriteAllText(Path.Combine(_projectDir, "b.txt"), "all good");
        var store = new LocalAgentFileStore(CreateWd());

        var results = await store.SearchAsync("", "error");

        results.Should().HaveCount(1);
        results[0].FileName.Should().Be("a.txt");
        results[0].MatchingLines.Should().HaveCount(1);
        results[0].MatchingLines[0].LineNumber.Should().Be(2);
        results[0].MatchingLines[0].Line.Should().Contain("error");
    }

    // IFileSystem methods

    [Fact]
    public async Task IFileSystem_ReadTextFileAsync_Works()
    {
        File.WriteAllText(Path.Combine(_projectDir, "ifile.txt"), "ifs content");
        IFileSystem fs = new LocalAgentFileStore(CreateWd());

        var content = await fs.ReadTextFileAsync("ifile.txt");

        content.Should().Be("ifs content");
    }

    [Fact]
    public async Task IFileSystem_WriteTextFileAsync_Works()
    {
        IFileSystem fs = new LocalAgentFileStore(CreateWd());

        await fs.WriteTextFileAsync("ifs-write.txt", "written");

        File.Exists(Path.Combine(_projectDir, "ifs-write.txt")).Should().BeTrue();
    }

    [Fact]
    public void IFileSystem_FindFiles_ReturnsMatchingFiles()
    {
        Directory.CreateDirectory(Path.Combine(_projectDir, "src"));
        File.WriteAllText(Path.Combine(_projectDir, "src", "a.cs"), "");
        File.WriteAllText(Path.Combine(_projectDir, "src", "b.ts"), "");
        IFileSystem fs = new LocalAgentFileStore(CreateWd());

        var files = fs.FindFiles(_projectDir, "*.cs");

        files.Should().HaveCount(1);
        files[0].Should().EndWith("a.cs");
    }

    [Fact]
    public void IFileSystem_MatchesGlob_ReturnsCorrectResult()
    {
        IFileSystem fs = new LocalAgentFileStore(CreateWd());

        fs.MatchesGlob("src/app/test.ts", "*.ts").Should().BeTrue();
        fs.MatchesGlob("src/app/test.cs", "*.ts").Should().BeFalse();
    }

    [Fact]
    public void IFileSystem_GetMtimeMs_ReturnsFileTimestamp()
    {
        var path = Path.Combine(_projectDir, "timestamped.txt");
        File.WriteAllText(path, "data");
        IFileSystem fs = new LocalAgentFileStore(CreateWd());

        var expected = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds();
        var mtime = fs.GetMtimeMs(path);

        mtime.Should().Be(expected);
    }

    // Path traversal protection

    [Fact]
    public async Task ReadAsync_OutsideWorkingDir_ThrowsUnauthorizedAccess()
    {
        File.WriteAllText(Path.Combine(_outsideDir, "secret.txt"), "secret");
        var store = new LocalAgentFileStore(CreateWd());

        // Relative path that escapes via ..
        var act = async () => await store.ReadAsync("../outside/secret.txt");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task WriteAsync_OutsideWorkingDir_ThrowsUnauthorizedAccess()
    {
        var store = new LocalAgentFileStore(CreateWd());

        var act = async () => await store.WriteAsync("../outside/hack.txt", "hacked");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        File.Exists(Path.Combine(_outsideDir, "hack.txt")).Should().BeFalse();
    }
}
