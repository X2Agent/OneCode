using System.Text;
using OneCode.Core.Tools;
using OneCode.App.Tools;
using NSubstitute;

namespace OneCode.Tests;

// DeleteTool — deletion semantics, dry-run preview and path safety

public sealed class DeleteToolTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(
        Path.GetTempPath(), "DeleteToolTests_" + Guid.NewGuid().ToString("N")[..8]);

    public DeleteToolTests() => Directory.CreateDirectory(_tmpDir);
    public void Dispose() { try { Directory.Delete(_tmpDir, recursive: true); } catch { } }

    [Fact]
    public async Task Delete_ExistingFile_DeletesAndReports()
    {
        var file = Write("a.cs", "class Foo { }");
        var result = await RunDeleteAsync(file);

        result.Content.Should().NotStartWith("Error");
        result.Content.Should().Contain("File deleted");
        File.Exists(file).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_NonExistent_ReturnsError()
    {
        var result = await RunDeleteAsync(Path.Combine(_tmpDir, "missing.cs"));

        result.Content.Should().Contain("not found");
    }

    [Fact]
    public async Task Delete_EmptyDirectory_DeletesWithoutRecursive()
    {
        var dir = Path.Combine(_tmpDir, "empty-dir");
        Directory.CreateDirectory(dir);

        var result = await RunDeleteAsync(dir);

        result.Content.Should().NotStartWith("Error");
        Directory.Exists(dir).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_NonEmptyDirectory_WithoutRecursive_ReturnsErrorWithEntryCount()
    {
        var dir = Path.Combine(_tmpDir, "tree");
        Directory.CreateDirectory(dir);
        Write(Path.Combine("tree", "one.cs"), "1");
        Write(Path.Combine("tree", "two.cs"), "2");

        var result = await RunDeleteAsync(dir);

        result.Content.Should().Contain("not empty");
        result.Content.Should().Contain("2 direct entries");
        Directory.Exists(dir).Should().BeTrue("recursive=false must not delete a non-empty directory");
    }

    [Fact]
    public async Task Delete_NonEmptyDirectory_WithRecursive_DeletesTree()
    {
        var dir = Path.Combine(_tmpDir, "tree2");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        Write(Path.Combine("tree2", "root.cs"), "r");
        Write(Path.Combine("tree2", "sub", "nested.cs"), "n");

        var result = await RunDeleteAsync(dir, recursive: true);

        result.Content.Should().NotStartWith("Error");
        Directory.Exists(dir).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_FileDryRun_DoesNotTouchDisk()
    {
        var file = Write("keep.cs", "content");
        var result = await RunDeleteAsync(file, dryRun: true);

        result.Content.Should().Contain("[Dry run]");
        result.Content.Should().Contain("Would delete file");
        File.Exists(file).Should().BeTrue("dry_run must not delete");
        (await File.ReadAllTextAsync(file)).Should().Be("content");
    }

    [Fact]
    public async Task Delete_DirectoryDryRun_ReportsTotalsWithoutTouchingDisk()
    {
        var dir = Path.Combine(_tmpDir, "tree3");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        var f1 = Write(Path.Combine("tree3", "a.cs"), "a");
        var f2 = Write(Path.Combine("tree3", "sub", "b.cs"), "b");

        var result = await RunDeleteAsync(dir, recursive: true, dryRun: true);

        result.Content.Should().Contain("[Dry run]");
        result.Content.Should().Contain("2 direct entries");
        result.Content.Should().Contain("2 files in total");
        File.Exists(f1).Should().BeTrue();
        File.Exists(f2).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_PathTraversal_OutsideWorkingDirectory_ReturnsError()
    {
        var outside = Path.Combine(Path.GetTempPath(), "DeleteToolTests_outside_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outside);
        try
        {
            var result = await RunDeleteAsync(Path.Combine(_tmpDir, "..", Path.GetFileName(outside)));

            result.Content.Should().StartWith("Error");
            Directory.Exists(outside).Should().BeTrue("path traversal must be rejected");
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Delete_WorkingDirectoryItself_ReturnsError()
    {
        var result = await RunDeleteAsync(".");

        result.Content.Should().StartWith("Error");
        Directory.Exists(_tmpDir).Should().BeTrue();
    }

    // Helpers

    private string Write(string relativeName, string content)
    {
        var path = Path.IsPathRooted(relativeName)
            ? relativeName
            : Path.Combine(_tmpDir, relativeName);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private IWorkingDirectoryAccessor CreateWd()
    {
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(_tmpDir);
        return wd;
    }

    private Task<ToolResult> RunDeleteAsync(string path, bool recursive = false, bool dryRun = false)
    {
        var tool = new DeleteTool(CreateWd(), ssh: null!);
        return tool.DeleteAsync(path, recursive, dryRun, TestContext.Current.CancellationToken);
    }
}
