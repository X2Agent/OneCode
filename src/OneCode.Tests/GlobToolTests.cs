using NSubstitute;
using OneCode.App.Tools;
using OneCode.Core.Tools;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="GlobTool"/> — covers path-safety boundary checks,
/// pattern matching, recursive globs, FileIgnore exclusions,
/// and error handling.
/// </summary>
public sealed class GlobToolTests : IDisposable
{
    private readonly string _sandboxDir;
    private readonly string _projectDir;
    private readonly string _outsideDir;

    public GlobToolTests()
    {
        _sandboxDir = Path.Combine(Path.GetTempPath(), $"GlobToolTests_{Guid.NewGuid():N}");
        _projectDir = Path.Combine(_sandboxDir, "project");
        _outsideDir = Path.Combine(_sandboxDir, "outside");
        Directory.CreateDirectory(_projectDir);
        Directory.CreateDirectory(_outsideDir);
        Directory.CreateDirectory(Path.Combine(_projectDir, "src"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandboxDir, recursive: true); } catch { /* best effort */ }
    }

    private IWorkingDirectoryAccessor CreateWd(string? workingDir = null)
    {
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(workingDir ?? _projectDir);
        return wd;
    }

    private void WriteFile(string relativeName, string content = "// placeholder")
    {
        var path = Path.Combine(_projectDir, relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void AssertRejected(ToolResult result)
    {
        result.IsError.Should().BeTrue();
        result.Content.Should().Match(s => s.Contains("outside the working directory")
                                || s.Contains("protected system directory")
                                || s.Contains("Access denied"),
            "path must be rejected for traversal or protected-dir access");
    }

    // Path safety

    [Theory]
    [InlineData("../../outside")]
    [InlineData("../../../etc/passwd")]
    [InlineData("../outside/secret")]
    public async Task GlobAsync_TraversalPath_ReturnsError(string traversal)
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/a.cs");
        var tool = new GlobTool(CreateWd());

        var result = await tool.GlobAsync("*.cs", path: traversal, ct: ct);

        AssertRejected(result);
    }

    [Fact]
    public async Task GlobAsync_AbsolutePathOutsideWorkingDir_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/a.cs");
        var tool = new GlobTool(CreateWd());

        var result = await tool.GlobAsync("*.cs", path: _outsideDir, ct: ct);

        AssertRejected(result);
    }

    [Fact]
    public async Task GlobAsync_PathInsideWorkingDir_FindsFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/a.cs");
        WriteFile("src/b.cs");
        var tool = new GlobTool(CreateWd());

        var result = await tool.GlobAsync("*.cs", path: "src", ct: ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Found 2 files");
        result.Content.Should().Contain("a.cs");
        result.Content.Should().Contain("b.cs");
    }

    // Pattern matching

    [Fact]
    public async Task GlobAsync_BasicPattern_MatchesOnlySpecifiedExtension()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/code.cs");
        WriteFile("src/script.ts");
        var tool = new GlobTool(CreateWd());

        var result = await tool.GlobAsync("*.cs", path: "src", ct: ct);

        result.Content.Should().Contain("code.cs");
        result.Content.Should().NotContain("script.ts");
    }

    [Fact]
    public async Task GlobAsync_RecursivePattern_MatchesNestedFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/top.cs");
        WriteFile("src/sub/deep.cs");
        var tool = new GlobTool(CreateWd());

        var result = await tool.GlobAsync("**/*.cs", path: "src", ct: ct);

        result.Content.Should().Contain("top.cs");
        result.Content.Should().Contain("deep.cs");
    }

    [Fact]
    public async Task GlobAsync_EmptyDirectory_ReturnsNoFilesMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = new GlobTool(CreateWd());

        var result = await tool.GlobAsync("*.cs", path: "src", ct: ct);

        result.Content.Should().Contain("No files matching");
    }

    [Fact]
    public async Task GlobAsync_NonExistentDirectory_ReturnsDirectoryNotFoundError()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = new GlobTool(CreateWd());

        var result = await tool.GlobAsync("*.cs", path: "does_not_exist", ct: ct);

        result.Content.Should().Contain("Directory not found");
    }

    [Fact]
    public async Task GlobAsync_EmptyPattern_ReturnsPatternRequiredError()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = new GlobTool(CreateWd());

        var result = await tool.GlobAsync("", path: "src", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("Pattern is required");
    }

    [Fact]
    public async Task GlobAsync_MultipleFiles_CountReportedInHeader()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/f1.cs");
        WriteFile("src/f2.cs");
        WriteFile("src/f3.cs");
        var tool = new GlobTool(CreateWd());

        var result = await tool.GlobAsync("*.cs", path: "src", ct: ct);

        result.Content.Should().Contain("Found 3 files");
    }

    // FileIgnore exclusions

    [Fact]
    public async Task GlobAsync_ExcludesBinAndObjDirectoriesByDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/real.cs");
        WriteFile("src/bin/generated.cs");
        WriteFile("src/obj/compiled.cs");
        var tool = new GlobTool(CreateWd());

        var result = await tool.GlobAsync("**/*.cs", path: "src", ct: ct);

        result.Content.Should().Contain("real.cs");
        result.Content.Should().NotContain("generated.cs");
        result.Content.Should().NotContain("compiled.cs");
    }

    [Fact]
    public async Task GlobAsync_SingleFile_FoundMessageUsesSingularForm()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/only.cs");
        var tool = new GlobTool(CreateWd());

        var result = await tool.GlobAsync("*.cs", path: "src", ct: ct);

        result.Content.Should().Contain("Found 1 file matching");
        result.Content.Should().NotContain("Found 1 files");
    }
}
