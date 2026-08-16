using NSubstitute;
using OneCode.App.Tools;
using OneCode.Core.Tools;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Abstractions;

namespace OneCode.Tests;

/// <summary>
/// Path traversal boundary tests — supplements work items 3.1 / 3.2 by verifying that
/// every file-access tool rejects paths that escape the working directory.
///
/// Note: <see cref="PathsHelper.SafeResolve(string, string)"/> may return one of two rejection messages
/// for an escaping path:
///   - "outside the working directory"  (generic traversal rejection)
///   - "protected system directory"      (when the resolved path also lands in a
///     platform-protected directory like AppData on Windows)
/// Both indicate the path was correctly rejected, so the helper below accepts either.
/// </summary>
[Collection(nameof(CurrentDirectoryCollection))]
public sealed class PathTraversalTests
{
    private readonly string _tempDir;
    private string? _originalWorkingDir;

    public PathTraversalTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"PathTraversalTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        // Create a subdirectory "outside" sibling to test escapes via ../
        Directory.CreateDirectory(Path.Combine(_tempDir, "project"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "outside"));
        // A subdir inside the project for positive-path tests (avoids passing the
        // working-dir root itself, which IsWithinDirectory treats as "not within"
        // due to its strict trailing-separator comparison).
        Directory.CreateDirectory(Path.Combine(_tempDir, "project", "subdir"));
    }

    private void SetWorkingDir(string path)
    {
        _originalWorkingDir = Environment.CurrentDirectory;
        Environment.CurrentDirectory = path;
    }

    private void RestoreWorkingDir()
    {
        if (_originalWorkingDir != null)
            Environment.CurrentDirectory = _originalWorkingDir;
    }

    private void Cleanup()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Asserts the result is a rejection error mentioning either traversal or protected dir.
    /// Tools now return a structured <see cref="ToolResult"/> whose <see cref="ToolResult.IsError"/>
    /// flag marks rejections; the underlying error text comes from <see cref="PathsHelper.SafeResolve(string, string)"/>
    /// and is NOT prefixed with "Error:" (only ReadTool wraps its errors with an "Error:" prefix).
    /// </summary>
    private static void AssertRejected(ToolResult result)
    {
        result.IsError.Should().BeTrue("the path must be rejected as a ToolResult error");
        result.Content.Should().Match(s => s.Contains("outside the working directory")
                                || s.Contains("protected system directory")
                                || s.Contains("Access denied"),
            "the path must be rejected either for traversal or for landing in a protected dir");
    }

    // PathsHelper.SafeResolve — exhaustive traversal patterns

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\..\\windows\\system32")]
    [InlineData("../../outside")]
    [InlineData("../outside/file.txt")]
    [InlineData("./../outside")]
    [InlineData("project/../../../outside")]
    public void SafeResolve_TraversalPattern_OutsideWorkingDir_ReturnsFailure(string traversal)
    {
        var workDir = Path.Combine(_tempDir, "project");
        try
        {
            var result = PathsHelper.SafeResolve(traversal, workDir);

            result.IsSuccess.Should().BeFalse();
            result.Error.Should().NotBeNullOrEmpty();
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void SafeResolve_AbsolutePathOutsideWorkingDir_ReturnsFailure()
    {
        var workDir = Path.Combine(_tempDir, "project");
        try
        {
            var outside = Path.Combine(_tempDir, "outside", "file.txt");

            var result = PathsHelper.SafeResolve(outside, workDir);

            result.IsSuccess.Should().BeFalse();
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void SafeResolve_PathInsideWorkingDir_ReturnsSuccess()
    {
        var workDir = Path.Combine(_tempDir, "project");
        try
        {
            var result = PathsHelper.SafeResolve("subdir/file.txt", workDir);

            result.IsSuccess.Should().BeTrue();
            result.Value.Should().StartWith(workDir);
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void SafeResolve_NormalPathWithinNestedSubdir_ReturnsSuccess()
    {
        var workDir = Path.Combine(_tempDir, "project");
        try
        {
            var result = PathsHelper.SafeResolve("a/b/c/d/file.txt", workDir);

            result.IsSuccess.Should().BeTrue();
            result.Value.Should().Contain("a/b/c/d/file.txt".Replace('/', Path.DirectorySeparatorChar));
        }
        finally { Cleanup(); }
    }

    // ReadTool — uses IWorkingDirectoryAccessor (mockable)

    [Fact]
    public async Task ReadTool_TraversalPath_ReturnsErrorString()
    {
        var workDir = Path.Combine(_tempDir, "project");
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(workDir);
        var tool = new ReadTool(wd, ssh: null!);
        try
        {
            var result = await tool.ReadAsync("../../../etc/passwd", ct: TestContext.Current.CancellationToken);

            AssertRejected(result);
        }
        finally { Cleanup(); }
    }

    [Fact]
    public async Task ReadTool_AbsolutePathOutsideWorkingDir_ReturnsErrorString()
    {
        var workDir = Path.Combine(_tempDir, "project");
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(workDir);
        var tool = new ReadTool(wd, ssh: null!);
        try
        {
            var outsidePath = Path.Combine(_tempDir, "outside", "file.txt");
            await File.WriteAllTextAsync(outsidePath, "secret", TestContext.Current.CancellationToken);

            var result = await tool.ReadAsync(outsidePath, ct: TestContext.Current.CancellationToken);

            AssertRejected(result);
        }
        finally { Cleanup(); }
    }

    [Fact]
    public async Task ReadTool_PathInsideWorkingDirButMissing_ReturnsFileNotFoundError()
    {
        var workDir = Path.Combine(_tempDir, "project");
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(workDir);
        var tool = new ReadTool(wd, ssh: null!);
        try
        {
            // Path is inside working dir (passes traversal check) but file does not exist
            var result = await tool.ReadAsync("subdir/missing_file.txt", ct: TestContext.Current.CancellationToken);

            result.Content.Should().StartWith("Error:");
            result.Content.Should().Contain("File not found");
        }
        finally { Cleanup(); }
    }

    [Fact]
    public async Task ReadTool_NormalFileInsideWorkingDir_ReturnsContent()
    {
        var workDir = Path.Combine(_tempDir, "project");
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(workDir);
        var tool = new ReadTool(wd, ssh: null!);
        try
        {
            var filePath = Path.Combine(workDir, "hello.txt");
            await File.WriteAllTextAsync(filePath, "hello world", TestContext.Current.CancellationToken);

            var result = await tool.ReadAsync("hello.txt", ct: TestContext.Current.CancellationToken);

            result.Content.Should().NotStartWith("Error:");
            result.Content.Should().Contain("hello world");
        }
        finally { Cleanup(); }
    }

    // GrepTool — uses IWorkingDirectoryAccessor

    [Fact]
    public async Task GrepTool_SearchPathOutsideWorkingDir_ReturnsError()
    {
        var projectDir = Path.Combine(_tempDir, "project");
        var outsideDir = Path.Combine(_tempDir, "outside");
        var processRunner = Substitute.For<IProcessRunner>();
        var fileSystem = Substitute.For<IFileSystem>();
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(projectDir);
        var tool = new GrepTool(processRunner, fileSystem, wd);
        try
        {
            var outsideSearchPath = Path.Combine(outsideDir, "subdir");

            var result = await tool.SearchAsync("pattern", path: outsideSearchPath, ct: TestContext.Current.CancellationToken);

            AssertRejected(result);
            // Process runner / file system must NOT have been called — rejection happens first
            await processRunner.DidNotReceive().ExecuteAsync(
                Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<string>(), ct: Arg.Any<CancellationToken>());
        }
        finally
        {
            Cleanup();
        }
    }

    [Fact]
    public async Task GrepTool_RelativeTraversalPath_ReturnsError()
    {
        var projectDir = Path.Combine(_tempDir, "project");
        var processRunner = Substitute.For<IProcessRunner>();
        var fileSystem = Substitute.For<IFileSystem>();
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(projectDir);
        var tool = new GrepTool(processRunner, fileSystem, wd);
        try
        {
            var result = await tool.SearchAsync("pattern", path: "../../outside", ct: TestContext.Current.CancellationToken);

            AssertRejected(result);
        }
        finally
        {
            Cleanup();
        }
    }

    [Fact]
    public async Task GrepTool_SearchInsideWorkingDir_DoesNotReturnTraversalError()
    {
        var projectDir = Path.Combine(_tempDir, "project");
        // Create a real file inside the project's subdir for native fallback to find
        await File.WriteAllTextAsync(Path.Combine(projectDir, "subdir", "file.cs"), "Hello World", TestContext.Current.CancellationToken);

        // Force native fallback (no ripgrep)
        var processRunner = Substitute.For<IProcessRunner>();
        processRunner.CommandExistsAsync("rg").Returns(false);
        var fileSystem = Substitute.For<IFileSystem>();
        fileSystem.FindFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string[]>())
                  .Returns(new List<string> { Path.Combine(projectDir, "subdir", "file.cs") });
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(projectDir);
        var tool = new GrepTool(processRunner, fileSystem, wd);
        try
        {
            // Use "subdir" (a real subdirectory) rather than "." to avoid the
            // working-dir-root edge case in IsWithinDirectory's strict comparison.
            var result = await tool.SearchAsync("Hello", path: "subdir", ct: TestContext.Current.CancellationToken);

            result.Content.Should().NotStartWith("Error:");
            result.Content.Should().Contain("file.cs");
        }
        finally
        {
            Cleanup();
        }
    }

    // GlobTool — uses IWorkingDirectoryAccessor

    [Fact]
    public async Task GlobTool_TraversalPath_ReturnsError()
    {
        var projectDir = Path.Combine(_tempDir, "project");
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(projectDir);
        var tool = new GlobTool(wd);
        try
        {
            var result = await tool.GlobAsync("*.cs", path: "../../outside", ct: TestContext.Current.CancellationToken);

            AssertRejected(result);
        }
        finally
        {
            Cleanup();
        }
    }

    [Fact]
    public async Task GlobTool_AbsolutePathOutsideWorkingDir_ReturnsError()
    {
        var projectDir = Path.Combine(_tempDir, "project");
        var outsideDir = Path.Combine(_tempDir, "outside");
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(projectDir);
        var tool = new GlobTool(wd);
        try
        {
            var result = await tool.GlobAsync("*.cs", path: outsideDir, ct: TestContext.Current.CancellationToken);

            AssertRejected(result);
        }
        finally
        {
            Cleanup();
        }
    }

    [Fact]
    public async Task GlobTool_InsideWorkingDir_FindsFiles()
    {
        var projectDir = Path.Combine(_tempDir, "project");
        // Place files in the pre-created "subdir" so we can pass "subdir" as the
        // search path (which resolves strictly inside the working dir, avoiding the
        // working-dir-root edge case in IsWithinDirectory's strict comparison).
        await File.WriteAllTextAsync(Path.Combine(projectDir, "subdir", "a.cs"), "x", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(projectDir, "subdir", "b.cs"), "x", TestContext.Current.CancellationToken);
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(projectDir);
        var tool = new GlobTool(wd);
        try
        {
            var result = await tool.GlobAsync("*.cs", path: "subdir", ct: TestContext.Current.CancellationToken);

            result.Content.Should().NotStartWith("Error:");
            result.Content.Should().Contain("a.cs");
            result.Content.Should().Contain("b.cs");
        }
        finally
        {
            Cleanup();
        }
    }

    // FindReferencesTool — uses IWorkingDirectoryAccessor

    [Fact]
    public async Task FindReferencesTool_TraversalPath_ReturnsError()
    {
        var projectDir = Path.Combine(_tempDir, "project");
        var processRunner = Substitute.For<IProcessRunner>();
        var fileSystem = Substitute.For<IFileSystem>();
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(projectDir);
        var tool = new FindReferencesTool(processRunner, fileSystem, wd, null!, null!, null!);
        try
        {
            var result = await tool.FindAsync("MySymbol", path: "../../outside", ct: TestContext.Current.CancellationToken);

            AssertRejected(result);
        }
        finally
        {
            Cleanup();
        }
    }

    [Fact]
    public async Task FindReferencesTool_AbsolutePathOutsideWorkingDir_ReturnsError()
    {
        var projectDir = Path.Combine(_tempDir, "project");
        var outsideDir = Path.Combine(_tempDir, "outside");
        var processRunner = Substitute.For<IProcessRunner>();
        var fileSystem = Substitute.For<IFileSystem>();
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(projectDir);
        var tool = new FindReferencesTool(processRunner, fileSystem, wd, null!, null!, null!);
        try
        {
            var result = await tool.FindAsync("MySymbol", path: outsideDir, ct: TestContext.Current.CancellationToken);

            AssertRejected(result);
            // Native/ripgrep search must NOT have been invoked
            await processRunner.DidNotReceive().ExecuteAsync(
                Arg.Any<string>(), Arg.Any<string[]>(), Arg.Any<string>(), ct: Arg.Any<CancellationToken>());
        }
        finally
        {
            Cleanup();
        }
    }

    [Fact]
    public async Task FindReferencesTool_InsideWorkingDir_DoesNotReturnTraversalError()
    {
        var projectDir = Path.Combine(_tempDir, "project");
        await File.WriteAllTextAsync(Path.Combine(projectDir, "subdir", "file.cs"), "MySymbol()", TestContext.Current.CancellationToken);

        var processRunner = Substitute.For<IProcessRunner>();
        processRunner.CommandExistsAsync("rg").Returns(false);
        var fileSystem = Substitute.For<IFileSystem>();
        fileSystem.FindFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string[]>())
                  .Returns(new List<string> { Path.Combine(projectDir, "subdir", "file.cs") });
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(projectDir);
        var tool = new FindReferencesTool(processRunner, fileSystem, wd, null!, null!, null!);
        try
        {
            // Search in "subdir" (strictly inside working dir) to avoid "." edge case
            var result = await tool.FindAsync("MySymbol", path: "subdir", ct: TestContext.Current.CancellationToken);

            result.Content.Should().NotStartWith("Error:");
            result.Content.Should().Contain("file.cs");
            result.Content.Should().Contain("MySymbol");
        }
        finally
        {
            Cleanup();
        }
    }

    // WriteTool — destructive write must be blocked for traversal paths

    [Theory]
    [InlineData("../../outside/escape.txt")]
    [InlineData("../../../etc/cron.d/backdoor")]
    [InlineData("../sibling/.env")]
    public async Task WriteTool_TraversalPath_ReturnsErrorAndDoesNotWrite(string traversalPath)
    {
        var projectDir = Path.Combine(_tempDir, "project");
        var outsideDir = Path.Combine(_tempDir, "outside");
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(projectDir);
        var tool = new WriteTool(notifier: NoOpLspNotifier.Instance, wd, ssh: null!);
        try
        {
            var result = await tool.WriteAsync(traversalPath, "malicious content", ct: TestContext.Current.CancellationToken);

            AssertRejected(result);
            // Critical: no file should have been written outside the project dir
            Directory.GetFiles(outsideDir, "*", SearchOption.AllDirectories)
                     .Should().NotContain(f => f.EndsWith("escape.txt") || f.EndsWith("backdoor") || f.EndsWith(".env"),
                         "traversal path must not create any file outside working dir");
        }
        finally
        {
            Cleanup();
        }
    }

    [Fact]
    public async Task WriteTool_AbsolutePathOutsideWorkingDir_ReturnsErrorAndDoesNotWrite()
    {
        var projectDir = Path.Combine(_tempDir, "project");
        var outsideDir = Path.Combine(_tempDir, "outside");
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(projectDir);
        var tool = new WriteTool(notifier: NoOpLspNotifier.Instance, wd, ssh: null!);
        try
        {
            var outsideFile = Path.Combine(outsideDir, "absolute-escape.txt");

            var result = await tool.WriteAsync(outsideFile, "content", ct: TestContext.Current.CancellationToken);

            AssertRejected(result);
            File.Exists(outsideFile).Should().BeFalse("absolute path outside working dir must not be written");
        }
        finally
        {
            Cleanup();
        }
    }

    [Fact]
    public async Task WriteTool_PathInsideWorkingDir_WritesSuccessfully()
    {
        var projectDir = Path.Combine(_tempDir, "project");
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(projectDir);
        var tool = new WriteTool(notifier: NoOpLspNotifier.Instance, wd, ssh: null!);
        try
        {
            var result = await tool.WriteAsync("subdir/legit.txt", "safe content", ct: TestContext.Current.CancellationToken);

            result.IsError.Should().BeFalse();
            File.Exists(Path.Combine(projectDir, "subdir", "legit.txt")).Should().BeTrue();
        }
        finally
        {
            Cleanup();
        }
    }

    // EditTool — destructive edit must be blocked for traversal paths

    [Theory]
    [InlineData("../../outside/escape.txt")]
    [InlineData("../../../etc/passwd")]
    [InlineData("../sibling/secret.txt")]
    public async Task EditTool_TraversalPath_ReturnsErrorAndDoesNotModify(string traversalPath)
    {
        var projectDir = Path.Combine(_tempDir, "project");
        var outsideDir = Path.Combine(_tempDir, "outside");
        // Pre-create files both inside (for control) and outside (to verify not modified)
        var outsideFile = Path.Combine(outsideDir, "escape.txt");
        await File.WriteAllTextAsync(outsideFile, "original secret", TestContext.Current.CancellationToken);

        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(projectDir);
        var tool = new EditTool(notifier: NoOpLspNotifier.Instance, wd, ssh: null!);
        try
        {
            var result = await tool.EditAsync(traversalPath, "original", "modified", ct: TestContext.Current.CancellationToken);

            AssertRejected(result);
            // Critical: file outside must not be modified
            (await File.ReadAllTextAsync(outsideFile)).Should().Be("original secret",
                "traversal path must not allow editing files outside working dir");
        }
        finally
        {
            Cleanup();
        }
    }

    [Fact]
    public async Task EditTool_AbsolutePathOutsideWorkingDir_ReturnsError()
    {
        var projectDir = Path.Combine(_tempDir, "project");
        var outsideDir = Path.Combine(_tempDir, "outside");
        var outsideFile = Path.Combine(outsideDir, "target.txt");
        await File.WriteAllTextAsync(outsideFile, "untouched", TestContext.Current.CancellationToken);

        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(projectDir);
        var tool = new EditTool(notifier: NoOpLspNotifier.Instance, wd, ssh: null!);
        try
        {
            var result = await tool.EditAsync(outsideFile, "untouched", "hacked", ct: TestContext.Current.CancellationToken);

            AssertRejected(result);
            (await File.ReadAllTextAsync(outsideFile)).Should().Be("untouched",
                "absolute path outside working dir must not be editable");
        }
        finally
        {
            Cleanup();
        }
    }

    // BashTool — referenced path traversal must be blocked before execution

    [Theory]
    [InlineData("cat ../../../outside/secret.txt")]
    [InlineData("rm ../../sibling/file.txt")]
    [InlineData("cp /etc/passwd ../../../outside/escape.txt")]
    public async Task BashTool_TraversalReferencedPath_ReturnsErrorWithoutExecution(string command)
    {
        var projectDir = Path.Combine(_tempDir, "project");
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(projectDir);
        var tool = new BashTool(wd, ssh: null!, shellExecutorManager: null!, sessionManager: null!);
        try
        {
            var result = await tool.ExecuteAsync(command, ct: TestContext.Current.CancellationToken);

            result.IsError.Should().BeTrue("commands referencing traversal paths must be rejected");
            result.Content.Should().Contain("outside the working directory");
            // No file outside the project should have been created or modified
            Directory.GetFiles(Path.Combine(_tempDir, "outside"), "*", SearchOption.AllDirectories)
                     .Should().BeEmpty("traversal command must not have side effects on outside files");
        }
        finally
        {
            Cleanup();
        }
    }

    [Fact]
    public async Task BashTool_AbsoluteReferencedPathOutsideWorkingDir_ReturnsError()
    {
        var projectDir = Path.Combine(_tempDir, "project");
        var outsideFile = Path.Combine(_tempDir, "outside", "absolute.txt");
        await File.WriteAllTextAsync(outsideFile, "original", TestContext.Current.CancellationToken);

        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(projectDir);
        var tool = new BashTool(wd, ssh: null!, shellExecutorManager: null!, sessionManager: null!);
        try
        {
            var result = await tool.ExecuteAsync($"cat {outsideFile}", ct: TestContext.Current.CancellationToken);

            result.IsError.Should().BeTrue("absolute path outside working dir must be rejected");
            result.Content.Should().Contain("outside the working directory");
        }
        finally
        {
            Cleanup();
        }
    }
}
