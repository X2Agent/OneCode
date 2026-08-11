using NSubstitute;
using OneCode.App.Tools;
using OneCode.Core.Tools;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Abstractions;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="GrepTool"/> — covers path-safety boundary checks,
/// output modes, context lines, multiline matching, exclude globs,
/// head-limit truncation, and ripgrep delegation.
/// </summary>
public sealed class GrepToolTests : IDisposable
{
    private readonly string _sandboxDir;
    private readonly string _projectDir;
    private readonly string _outsideDir;

    public GrepToolTests()
    {
        _sandboxDir = Path.Combine(Path.GetTempPath(), $"GrepToolTests_{Guid.NewGuid():N}");
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

    private string WriteFile(string relativeName, string content)
    {
        var path = Path.Combine(_projectDir, relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// Creates a GrepTool configured to use the native C# fallback (no ripgrep)
    /// with a real <see cref="LocalAgentFileStore"/> so file I/O is exercised end-to-end.
    /// </summary>
    private GrepTool CreateNativeTool()
    {
        var processRunner = Substitute.For<IProcessRunner>();
        processRunner.CommandExistsAsync("rg").Returns(false);
        return new GrepTool(processRunner, new LocalAgentFileStore(CreateWd()), CreateWd());
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
    [InlineData("../outside/secret.txt")]
    public async Task SearchAsync_TraversalPath_ReturnsError(string traversal)
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/file.cs", "Hello World");
        var tool = CreateNativeTool();

        var result = await tool.SearchAsync("Hello", path: traversal, ct: ct);

        AssertRejected(result);
    }

    [Fact]
    public async Task SearchAsync_AbsolutePathOutsideWorkingDir_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/file.cs", "Hello World");
        var tool = CreateNativeTool();

        var result = await tool.SearchAsync("Hello", path: _outsideDir, ct: ct);

        AssertRejected(result);
    }

    [Fact]
    public async Task SearchAsync_PathInsideWorkingDir_ReturnsSearchResults()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/file.cs", "Hello World");
        var tool = CreateNativeTool();

        var result = await tool.SearchAsync("Hello", path: "src", ct: ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("file.cs");
    }

    [Fact]
    public async Task SearchAsync_NonExistentPathInsideWorkingDir_ReturnsPathDoesNotExist()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = CreateNativeTool();

        var result = await tool.SearchAsync("pattern", path: "nonexistent_subdir", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("Path does not exist");
    }

    // Output modes

    [Fact]
    public async Task SearchAsync_FilesWithMatchesMode_ReturnsFileList()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/a.cs", "MATCH line");
        WriteFile("src/b.cs", "no match here");
        var tool = CreateNativeTool();

        var result = await tool.SearchAsync("MATCH", path: "src", output_mode: "files_with_matches", ct: ct);

        result.Content.Should().Contain("Found 1 file");
        result.Content.Should().Contain("a.cs");
        result.Content.Should().NotContain("b.cs");
    }

    [Fact]
    public async Task SearchAsync_CountMode_ReturnsMatchCounts()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/cnt.cs", "MATCH\nMATCH\nMATCH\nother");
        var tool = CreateNativeTool();

        var result = await tool.SearchAsync("MATCH", path: "src", output_mode: "count", ct: ct);

        result.Content.Should().Contain("Found 3 matches across 1 files");
        result.Content.Should().Contain("cnt.cs:3");
    }

    [Fact]
    public async Task SearchAsync_ContentMode_ReturnsMatchingLinesWithLineNumbers()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/content.cs", "line1\nFIND_ME\nline3");
        var tool = CreateNativeTool();

        var result = await tool.SearchAsync("FIND_ME", path: "src", output_mode: "content", ct: ct);

        result.Content.Should().Contain("content.cs:2:FIND_ME");
        result.Content.Should().NotContain("line1");
        result.Content.Should().NotContain("line3");
    }

    [Fact]
    public async Task SearchAsync_CaseInsensitive_FindsLowercasePattern()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/case.cs", "Hello World");
        var tool = CreateNativeTool();

        var result = await tool.SearchAsync("HELLO", path: "src", output_mode: "content", i: true, ct: ct);

        result.Content.Should().Contain("Hello World");
    }

    [Fact]
    public async Task SearchAsync_NoMatches_ReturnsNoFilesFoundMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/empty.cs", "nothing here");
        var tool = CreateNativeTool();

        var result = await tool.SearchAsync("NONEXISTENT_PATTERN", path: "src", ct: ct);

        result.Content.Should().Contain("No files found");
    }

    // Context lines

    [Fact]
    public async Task SearchAsync_ContextAfter_IncludesLinesAfterMatch()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/ctx.cs", "line1\nMATCH\nafter1\nafter2\nline5");
        var tool = CreateNativeTool();

        var result = await tool.SearchAsync("MATCH", path: "src", output_mode: "content", A: 2, ct: ct);

        result.Content.Should().Contain("MATCH");
        result.Content.Should().Contain("after1");
        result.Content.Should().Contain("after2");
        result.Content.Should().NotContain("line5");
    }

    [Fact]
    public async Task SearchAsync_ContextBefore_IncludesLinesBeforeMatch()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/ctxb.cs", "line1\nbefore1\nMATCH\nline4");
        var tool = CreateNativeTool();

        var result = await tool.SearchAsync("MATCH", path: "src", output_mode: "content", B: 1, ct: ct);

        result.Content.Should().Contain("before1");
        result.Content.Should().NotContain("line1");
    }

    [Fact]
    public async Task SearchAsync_OverlappingContext_MergedNoSeparator()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/merge.cs", "line1\nMATCH_A\nshared\nMATCH_B\nline5");
        var tool = CreateNativeTool();

        var result = await tool.SearchAsync("MATCH_", path: "src", output_mode: "content", C: 1, ct: ct);

        result.Content.Should().NotContain("\n--\n");
        result.Content.Should().Contain("MATCH_A");
        result.Content.Should().Contain("shared");
        result.Content.Should().Contain("MATCH_B");
    }

    // Multiline

    [Fact]
    public async Task SearchAsync_Multiline_MatchesPatternSpanningLines()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/ml.cs", "public class\nMyMultiClass { }");
        var tool = CreateNativeTool();

        var result = await tool.SearchAsync("class\\nMyMultiClass", path: "src", output_mode: "files_with_matches", multiline: true, ct: ct);

        result.Content.Should().Contain("ml.cs");
    }

    // Exclude glob

    [Fact]
    public async Task SearchAsync_ExcludeGlob_ExcludesMatchingFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_projectDir, "src", "tests"));
        WriteFile("src/main.cs", "TARGET");
        WriteFile("src/tests/test.cs", "TARGET");
        var tool = CreateNativeTool();

        var result = await tool.SearchAsync("TARGET", path: "src", output_mode: "files_with_matches", exclude_glob: "tests/**", ct: ct);

        result.Content.Should().Contain("main.cs");
        result.Content.Should().NotContain("test.cs");
    }

    // Head limit / truncation

    [Fact]
    public async Task SearchAsync_HeadLimit_TruncatesAndReportsTotalCount()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 10; i++)
            WriteFile($"src/f{i}.cs", "MATCH");
        var tool = CreateNativeTool();

        var result = await tool.SearchAsync("MATCH", path: "src", output_mode: "files_with_matches", head_limit: 3, ct: ct);

        result.Content.Should().Contain("showing 3 of 10 total");
    }

    // Ripgrep delegation

    [Fact]
    public async Task SearchAsync_RipgrepAvailable_DelegatesToRgAndStripsPathPrefix()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/rg.cs", "found");
        var searchPath = Path.Combine(_projectDir, "src");
        var prefix = searchPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var processRunner = Substitute.For<IProcessRunner>();
        processRunner.CommandExistsAsync("rg").Returns(true);
        // Ripgrep returns full paths — GrepTool must strip the search-path prefix
        processRunner.ExecuteAsync("rg", Arg.Any<string[]>(), searchPath, ct: Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, prefix + "rg.cs:1:found", "", false));

        var tool = new GrepTool(processRunner, Substitute.For<IFileSystem>(), CreateWd());

        var result = await tool.SearchAsync("found", path: "src", output_mode: "content", ct: ct);

        result.Content.Should().Contain("rg.cs:1:found");
        result.Content.Should().NotContain(prefix);
    }

    [Fact]
    public async Task SearchAsync_RipgrepFailure_ReturnsErrorMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/rgfail.cs", "content");
        var searchPath = Path.Combine(_projectDir, "src");

        var processRunner = Substitute.For<IProcessRunner>();
        processRunner.CommandExistsAsync("rg").Returns(true);
        processRunner.ExecuteAsync("rg", Arg.Any<string[]>(), searchPath, ct: Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(2, "", "ripgrep error: invalid regex", false));

        var tool = new GrepTool(processRunner, Substitute.For<IFileSystem>(), CreateWd());

        var result = await tool.SearchAsync("pattern", path: "src", output_mode: "content", ct: ct);

        result.Content.Should().Contain("ripgrep error: invalid regex");
    }
}
