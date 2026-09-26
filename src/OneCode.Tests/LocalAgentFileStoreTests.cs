using NSubstitute;
using OneCode.Core.Tools;
using OneCode.Infrastructure;
using OneCode.Core.IO;


namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="LocalAgentFileStore"/> — verifies IFileSystem operations
/// and path traversal protection.
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

        await fs.WriteTextFileAsync("ifs-write.txt", "written", TestContext.Current.CancellationToken);

        File.Exists(Path.Combine(_projectDir, "ifs-write.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task IFileSystem_WriteTextFileAsync_CreatesParentDirectories()
    {
        IFileSystem fs = new LocalAgentFileStore(CreateWd());

        await fs.WriteTextFileAsync("sub/dir/file.txt", "content", TestContext.Current.CancellationToken);

        var path = Path.Combine(_projectDir, "sub", "dir", "file.txt");
        File.Exists(path).Should().BeTrue();
        (await File.ReadAllTextAsync(path)).Should().Be("content");
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
    public async Task ReadTextFileAsync_OutsideWorkingDir_ThrowsUnauthorizedAccess()
    {
        File.WriteAllText(Path.Combine(_outsideDir, "secret.txt"), "secret");
        IFileSystem fs = new LocalAgentFileStore(CreateWd());

        // Relative path that escapes via ..
        var act = async () => await fs.ReadTextFileAsync("../outside/secret.txt", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task WriteTextFileAsync_OutsideWorkingDir_ThrowsUnauthorizedAccess()
    {
        IFileSystem fs = new LocalAgentFileStore(CreateWd());

        var act = async () => await fs.WriteTextFileAsync("../outside/hack.txt", "hacked", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        File.Exists(Path.Combine(_outsideDir, "hack.txt")).Should().BeFalse();
    }
}
