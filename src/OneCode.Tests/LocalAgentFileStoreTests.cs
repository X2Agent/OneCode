using NSubstitute;
using OneCode.Core.Tools;
using OneCode.Infrastructure;


namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="LocalAgentFileStore"/> — verifies glob file enumeration
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

    [Fact]
    public void FindFiles_ReturnsMatchingFiles()
    {
        Directory.CreateDirectory(Path.Combine(_projectDir, "src"));
        File.WriteAllText(Path.Combine(_projectDir, "src", "a.cs"), "");
        File.WriteAllText(Path.Combine(_projectDir, "src", "b.ts"), "");
        var fs = new LocalAgentFileStore(CreateWd());

        var files = fs.FindFiles(_projectDir, "*.cs");

        files.Should().HaveCount(1);
        files[0].Should().EndWith("a.cs");
    }

    [Fact]
    public void FindFiles_MissingDirectory_ReturnsEmpty()
    {
        var fs = new LocalAgentFileStore(CreateWd());

        var files = fs.FindFiles(Path.Combine(_projectDir, "does-not-exist"), "*");

        files.Should().BeEmpty();
    }

    [Fact]
    public void FindFiles_OutsideWorkingDir_ThrowsUnauthorizedAccess()
    {
        File.WriteAllText(Path.Combine(_outsideDir, "secret.txt"), "secret");
        var fs = new LocalAgentFileStore(CreateWd());

        // Relative path that escapes via ..
        var act = () => fs.FindFiles("../outside", "*");

        act.Should().Throw<UnauthorizedAccessException>();
    }
}
