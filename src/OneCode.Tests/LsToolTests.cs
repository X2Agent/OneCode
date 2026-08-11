using NSubstitute;
using OneCode.App.Tools;
using OneCode.Core.Tools;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="LSTool"/> — covers path-safety boundary checks, directory listing,
/// hidden-file filtering, and generated-directory/extension exclusion.
/// </summary>
public sealed class LsToolTests : IDisposable
{
    private readonly string _sandboxDir;
    private readonly string _projectDir;
    private readonly string _outsideDir;

    public LsToolTests()
    {
        _sandboxDir = Path.Combine(Path.GetTempPath(), $"LsToolTests_{Guid.NewGuid():N}");
        _projectDir = Path.Combine(_sandboxDir, "project");
        _outsideDir = Path.Combine(_sandboxDir, "outside");
        Directory.CreateDirectory(_projectDir);
        Directory.CreateDirectory(_outsideDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandboxDir, recursive: true); } catch { /* best effort */ }
    }

    private IWorkingDirectoryAccessor CreateWd(string? workingDir = null)
    {
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(workingDir ?? _projectDir);
        wd.AdditionalDirectories.Returns(Array.Empty<string>());
        return wd;
    }

    private string CreateListingDir()
    {
        var dir = Path.Combine(_projectDir, "listing");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void WriteFileInListing(string fileName, string content = "content")
    {
        var listingDir = Path.Combine(_projectDir, "listing");
        var path = Path.Combine(listingDir, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void CreateDirInListing(string dirName)
    {
        Directory.CreateDirectory(Path.Combine(_projectDir, "listing", dirName));
    }

    // Path safety

    [Theory]
    [InlineData("../../outside")]
    [InlineData("../../../etc/passwd")]
    [InlineData("../outside/secret")]
    public async Task ListAsync_TraversalPath_ReturnsError(string traversal)
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = new LSTool(CreateWd());

        var result = await tool.ListAsync(traversal, ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().StartWith("Error:");
    }

    [Fact]
    public async Task ListAsync_AbsolutePathOutsideWorkingDir_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = new LSTool(CreateWd());

        var result = await tool.ListAsync(_outsideDir, ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().StartWith("Error:");
    }

    [Fact]
    public async Task ListAsync_NonExistentDirectory_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var tool = new LSTool(CreateWd());

        var result = await tool.ListAsync("does_not_exist", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Be("Directory not found: does_not_exist");
    }

    // Successful listing

    [Fact]
    public async Task ListAsync_ValidDirectory_ReturnsSuccessWithEntryCount()
    {
        var ct = TestContext.Current.CancellationToken;
        CreateListingDir();
        WriteFileInListing("alpha.txt");
        WriteFileInListing("beta.txt");
        CreateDirInListing("gamma");
        var tool = new LSTool(CreateWd());

        var result = await tool.ListAsync("listing", ct: ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().StartWith("Total: 3 entries");
        result.Content.Should().Contain("alpha.txt");
        result.Content.Should().Contain("beta.txt");
        result.Content.Should().Contain("gamma/");
    }

    // Hidden file filtering

    [Fact]
    public async Task ListAsync_DefaultMode_FiltersDotfiles()
    {
        var ct = TestContext.Current.CancellationToken;
        CreateListingDir();
        WriteFileInListing("visible.txt");
        WriteFileInListing(".hidden");
        var tool = new LSTool(CreateWd());

        var result = await tool.ListAsync("listing", all: false, ct: ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Total: 1 entries");
        result.Content.Should().Contain("visible.txt");
        result.Content.Should().NotContain(".hidden");
    }

    [Fact]
    public async Task ListAsync_AllTrue_IncludesDotfiles()
    {
        var ct = TestContext.Current.CancellationToken;
        CreateListingDir();
        WriteFileInListing("visible.txt");
        WriteFileInListing(".hidden");
        var tool = new LSTool(CreateWd());

        var result = await tool.ListAsync("listing", all: true, ct: ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().StartWith("Total: 2 entries (including hidden)");
        result.Content.Should().Contain("visible.txt");
        result.Content.Should().Contain(".hidden");
    }

    // Generated directory/extension filtering

    [Fact]
    public async Task ListAsync_DefaultMode_FiltersGeneratedDirectories()
    {
        var ct = TestContext.Current.CancellationToken;
        CreateListingDir();
        CreateDirInListing("src");
        WriteFileInListing("src/real.cs");
        CreateDirInListing("bin");
        CreateDirInListing("obj");
        CreateDirInListing("node_modules");
        var tool = new LSTool(CreateWd());

        var result = await tool.ListAsync("listing", all: false, ct: ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("src/");
        result.Content.Should().NotContain("bin/");
        result.Content.Should().NotContain("obj/");
        result.Content.Should().NotContain("node_modules/");
    }

    [Fact]
    public async Task ListAsync_DefaultMode_FiltersGeneratedExtensions()
    {
        var ct = TestContext.Current.CancellationToken;
        CreateListingDir();
        WriteFileInListing("app.cs");
        WriteFileInListing("build.dll");
        var tool = new LSTool(CreateWd());

        var result = await tool.ListAsync("listing", all: false, ct: ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("app.cs");
        result.Content.Should().NotContain("build.dll");
        result.Content.Should().Contain("Total: 1 entries");
    }

    [Fact]
    public async Task ListAsync_AllTrue_ShowsGeneratedEntries()
    {
        var ct = TestContext.Current.CancellationToken;
        CreateListingDir();
        WriteFileInListing("app.cs");
        WriteFileInListing("build.dll");
        CreateDirInListing("bin");
        var tool = new LSTool(CreateWd());

        var result = await tool.ListAsync("listing", all: true, ct: ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("app.cs");
        result.Content.Should().Contain("build.dll");
        result.Content.Should().Contain("bin/");
    }
}
