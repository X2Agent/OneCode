using NSubstitute;
using OneCode.App.Tools;
using OneCode.Core.IO;
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
    public async Task GlobAsync_UserIgnoreFile_ExcludesMatchingPaths()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/keep.cs");
        WriteFile("cache/drop.cs");
        WriteFile("src/data/local.sqlite");
        WriteFile(".env.local");

        File.WriteAllText(Path.Combine(_projectDir, ".onecodeignore"), "cache/\n*.sqlite\n.env.*");

        var wd = CreateWd();
        var fileSystem = Substitute.For<IFileSystem>();
        fileSystem.GetMtimeMs(Arg.Any<string>()).Returns(call =>
        {
            var path = call.Arg<string>();
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
        });
        fileSystem.ReadTextFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var path = call.Arg<string>();
                return File.Exists(path)
                    ? Task.FromResult<string?>(File.ReadAllText(path))
                    : Task.FromResult<string?>(null);
            });
        var tool = new GlobTool(wd, new WorkspaceIgnoreProvider(wd, fileSystem));

        var result = await tool.GlobAsync("**/*", ct: ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("keep.cs");
        result.Content.Should().NotContain("drop.cs");        // 目录规则 cache/
        result.Content.Should().NotContain("local.sqlite");   // 文件规则 *.sqlite
        result.Content.Should().NotContain(".env.local");     // 点文件规则 .env.*
    }

    [Fact]
    public async Task GlobAsync_UserIgnoreFile_RootAnchoredRulesRespectedFromSubdirectorySearch()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/keep.cs");
        WriteFile("src/generated/artifact.cs");   // 子目录同名目录：不应被根锚定规则误伤
        WriteFile("generated/artifact.cs");       // 根级目录：应被 `/generated/` 命中

        File.WriteAllText(Path.Combine(_projectDir, ".onecodeignore"), "/generated/\n");

        var wd = CreateWd();
        var fileSystem = Substitute.For<IFileSystem>();
        fileSystem.GetMtimeMs(Arg.Any<string>()).Returns(call =>
        {
            var path = call.Arg<string>();
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
        });
        fileSystem.ReadTextFileAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var path = call.Arg<string>();
                return File.Exists(path)
                    ? Task.FromResult<string?>(File.ReadAllText(path))
                    : Task.FromResult<string?>(null);
            });
        var tool = new GlobTool(wd, new WorkspaceIgnoreProvider(wd, fileSystem));

        var result = await tool.GlobAsync("**/*.cs", path: "src", ct: ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("keep.cs");
        result.Content.Should().Contain("artifact.cs");  // `src/generated/` 不受根锚定规则影响；未修复时 matcher 返回的 src 相对路径会被误判为根级 `/generated/`
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
