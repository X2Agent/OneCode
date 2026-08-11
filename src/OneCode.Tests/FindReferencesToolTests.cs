using NSubstitute;
using OneCode.App.Tools;
using OneCode.Core.Tools;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Abstractions;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="FindReferencesTool"/> — covers path-safety boundary
/// checks, exact-word vs. substring matching, exclude globs,
/// max-results truncation, and empty-symbol validation.
/// </summary>
public sealed class FindReferencesToolTests : IDisposable
{
    private readonly string _sandboxDir;
    private readonly string _projectDir;
    private readonly string _outsideDir;

    public FindReferencesToolTests()
    {
        _sandboxDir = Path.Combine(Path.GetTempPath(), $"FindRefTests_{Guid.NewGuid():N}");
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

    private void WriteFile(string relativeName, string content)
    {
        var path = Path.Combine(_projectDir, relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>
    /// Creates a FindReferencesTool configured to use the native C# fallback (no ripgrep)
    /// with a real <see cref="LocalAgentFileStore"/>.
    /// </summary>
    private FindReferencesTool CreateNativeTool()
    {
        var processRunner = Substitute.For<IProcessRunner>();
        processRunner.CommandExistsAsync("rg").Returns(false);
        return new FindReferencesTool(processRunner, new LocalAgentFileStore(CreateWd()), CreateWd(), null!, null!, null!);
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
    public async Task FindAsync_TraversalPath_ReturnsError(string traversal)
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/a.cs", "MySymbol()");
        var tool = CreateNativeTool();

        var result = await tool.FindAsync("MySymbol", path: traversal, ct: ct);

        AssertRejected(result);
    }

    [Fact]
    public async Task FindAsync_AbsolutePathOutsideWorkingDir_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/a.cs", "MySymbol()");
        var tool = CreateNativeTool();

        var result = await tool.FindAsync("MySymbol", path: _outsideDir, ct: ct);

        AssertRejected(result);
    }

    [Fact]
    public async Task FindAsync_PathInsideWorkingDir_FindsReferences()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/svc.cs", "var x = MyService.Get();");
        var tool = CreateNativeTool();

        var result = await tool.FindAsync("MyService", path: "src", ct: ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("svc.cs");
        result.Content.Should().Contain("MyService");
    }

    // Symbol matching

    [Fact]
    public async Task FindAsync_ExactWord_DoesNotMatchSubstringOccurrence()
    {
        var ct = TestContext.Current.CancellationToken;
        // "MyServiceHelper" contains "MyService" as a substring but not as a whole word
        WriteFile("src/sub.cs", "var x = MyServiceHelper.Create();");
        var tool = CreateNativeTool();

        var result = await tool.FindAsync("MyService", path: "src", exactWord: true, ct: ct);

        result.Content.Should().Contain("No references found");
    }

    [Fact]
    public async Task FindAsync_ExactWordFalse_MatchesSubstring()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/sub.cs", "var x = MyServiceHelper.Create();");
        var tool = CreateNativeTool();

        var result = await tool.FindAsync("MyService", path: "src", exactWord: false, ct: ct);

        result.Content.Should().Contain("sub.cs");
        result.Content.Should().Contain("MyServiceHelper");
    }

    [Fact]
    public async Task FindAsync_NoResults_ReturnsNotFoundMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/empty.cs", "var x = 42;");
        var tool = CreateNativeTool();

        var result = await tool.FindAsync("NonExistentSymbol", path: "src", ct: ct);

        result.Content.Should().Contain("No references found");
        result.Content.Should().Contain("NonExistentSymbol");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FindAsync_EmptyOrWhitespaceSymbol_ReturnsSymbolRequiredError(string symbol)
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = CreateNativeTool();

        var result = await tool.FindAsync(symbol, path: "src", ct: ct);

        result.Content.Should().Contain("symbol is required");
    }

    // Exclude glob

    [Fact]
    public async Task FindAsync_ExcludeGlob_ExcludesMatchingFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_projectDir, "src", "tests"));
        WriteFile("src/main.cs", "var x = Target.Get();");
        WriteFile("src/tests/test.cs", "var x = Target.Get();");
        var tool = CreateNativeTool();

        var result = await tool.FindAsync("Target", path: "src", exclude_glob: "tests/**", ct: ct);

        result.Content.Should().Contain("main.cs");
        result.Content.Should().NotContain("test.cs");
    }

    // Max results truncation

    [Fact]
    public async Task FindAsync_MaxResults_TruncatesAndReportsTotal()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 20; i++)
            WriteFile($"src/f{i}.cs", $"var x = TargetSym.M{i}();");
        var tool = CreateNativeTool();

        var result = await tool.FindAsync("TargetSym", path: "src", max_results: 5, ct: ct);

        result.Content.Should().Contain("Showing first 5 of 20");
    }

    // Multiple references across files

    [Fact]
    public async Task FindAsync_MultipleFiles_AllReferencesReported()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/a.cs", "var a = SharedSymbol();");
        WriteFile("src/b.cs", "var b = SharedSymbol();");
        WriteFile("src/c.cs", "var c = SharedSymbol();");
        var tool = CreateNativeTool();

        var result = await tool.FindAsync("SharedSymbol", path: "src", ct: ct);

        result.Content.Should().Contain("Found 3 references");
        result.Content.Should().Contain("a.cs");
        result.Content.Should().Contain("b.cs");
        result.Content.Should().Contain("c.cs");
    }

    // Ripgrep delegation

    [Fact]
    public async Task FindAsync_RipgrepAvailable_DelegatesToRgAndStripsPathPrefix()
    {
        var ct = TestContext.Current.CancellationToken;
        WriteFile("src/rg.cs", "var x = MySymbol();");
        var searchPath = Path.Combine(_projectDir, "src");
        var prefix = searchPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var processRunner = Substitute.For<IProcessRunner>();
        processRunner.CommandExistsAsync("rg").Returns(true);
        processRunner.ExecuteAsync("rg", Arg.Any<string[]>(), searchPath, ct: Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, prefix + "rg.cs:1:var x = MySymbol();", "", false));

        var tool = new FindReferencesTool(processRunner, Substitute.For<IFileSystem>(), CreateWd(), null!, null!, null!);

        var result = await tool.FindAsync("MySymbol", path: "src", ct: ct);

        result.Content.Should().Contain("rg.cs:1:");
        result.Content.Should().NotContain(prefix);
    }
}
