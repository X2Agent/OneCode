using System.Text.Json;
using OneCode.Core.Lsp;
using OneCode.Core.Tools;
using OneCode.Infrastructure.Abstractions;
using OneCode.App.Tools;
using NSubstitute;

namespace OneCode.Tests;

public sealed class SymbolSearchToolTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(
        Path.GetTempPath(), "SymbolSearchTool_" + Guid.NewGuid().ToString("N")[..8]);

    public SymbolSearchToolTests() => Directory.CreateDirectory(_tmpDir);
    public void Dispose() { try { Directory.Delete(_tmpDir, recursive: true); } catch { } }

    [Fact]
    public async Task ExecuteAsync_NullIndexService_ReturnsError()
    {
        var sut = new SymbolSearchTool(null!, Substitute.For<IWorkingDirectoryAccessor>(), Substitute.For<ILspServerManager>());
        var result = await sut.SymbolSearchAsync("Foo");
        result.Content.Should().Contain("unavailable");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyQuery_ReturnsError()
    {
        var svc = Substitute.For<ICodeIndexService>();
        svc.LastIndexedAt.Returns((DateTimeOffset?)DateTimeOffset.UtcNow);
        var sut = new SymbolSearchTool(svc, Substitute.For<IWorkingDirectoryAccessor>(), Substitute.For<ILspServerManager>());

        var result = await sut.SymbolSearchAsync("");
        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("'query' is required");
    }

    [Fact]
    public async Task ExecuteAsync_NoResults_ReturnsNotFoundMessage()
    {
        var svc = Substitute.For<ICodeIndexService>();
        svc.LastIndexedAt.Returns((DateTimeOffset?)DateTimeOffset.UtcNow);
        svc.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new List<CodeSymbolMatch>());

        var sut = new SymbolSearchTool(svc, Substitute.For<IWorkingDirectoryAccessor>(), Substitute.For<ILspServerManager>());
        var result = await sut.SymbolSearchAsync("NonExistent");

        result.Content.Should().Contain("NonExistent");
    }

    [Fact]
    public async Task ExecuteAsync_WithResults_ReturnsJsonWithSummaryAndResults()
    {
        var symbol = new CodeSymbol("MyClass", "class", "/src/MyClass.cs", 5, 1);
        var matches = new[] { new CodeSymbolMatch(symbol, 1.0) };

        var svc = Substitute.For<ICodeIndexService>();
        svc.LastIndexedAt.Returns((DateTimeOffset?)DateTimeOffset.UtcNow);
        svc.SymbolCount.Returns(42);
        svc.Search("MyClass", Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(matches);

        var sut = new SymbolSearchTool(svc, Substitute.For<IWorkingDirectoryAccessor>(), Substitute.For<ILspServerManager>());
        var result = await sut.SymbolSearchAsync("MyClass");

        using var doc = JsonDocument.Parse(result.Content);
        var json = doc.RootElement;
        json.GetProperty("summary").GetString().Should().Contain("MyClass");
        json.GetProperty("results").GetArrayLength().Should().Be(1);

        var first = json.GetProperty("results")[0];
        first.GetProperty("name").GetString().Should().Be("MyClass");
        first.GetProperty("kind").GetString().Should().Be("class");
        first.GetProperty("line").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task ExecuteAsync_KindParam_PassedToIndexService()
    {
        var svc = Substitute.For<ICodeIndexService>();
        svc.LastIndexedAt.Returns((DateTimeOffset?)DateTimeOffset.UtcNow);
        svc.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new List<CodeSymbolMatch>());

        var sut = new SymbolSearchTool(svc, Substitute.For<IWorkingDirectoryAccessor>(), Substitute.For<ILspServerManager>());
        var result = await sut.SymbolSearchAsync("Svc", kind: "interface");

        result.IsError.Should().BeFalse("valid kind filter should not produce error");
        svc.Received(1).Search("Svc", Arg.Any<int>(), "interface", Arg.Any<string?>());
    }

    [Fact]
    public async Task ExecuteAsync_MaxResults_ClampedTo100()
    {
        var svc = Substitute.For<ICodeIndexService>();
        svc.LastIndexedAt.Returns((DateTimeOffset?)DateTimeOffset.UtcNow);
        svc.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new List<CodeSymbolMatch>());

        var sut = new SymbolSearchTool(svc, Substitute.For<IWorkingDirectoryAccessor>(), Substitute.For<ILspServerManager>());
        var result = await sut.SymbolSearchAsync("X", maxResults: 9999);

        result.IsError.Should().BeFalse("clamped max should not produce error");
        svc.Received(1).Search("X", 100, Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ExecuteAsync_RelativePath_ResolvedToAbsolute()
    {
        var svc = Substitute.For<ICodeIndexService>();
        svc.LastIndexedAt.Returns((DateTimeOffset?)DateTimeOffset.UtcNow);
        svc.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new List<CodeSymbolMatch>());

        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(_tmpDir);
        var sut = new SymbolSearchTool(svc, wd, Substitute.For<ILspServerManager>());
        var result = await sut.SymbolSearchAsync("X", path: "src");

        result.IsError.Should().BeFalse("resolved path should not produce error");
        var expectedAbsolute = Path.GetFullPath(Path.Combine(_tmpDir, "src"));
        svc.Received(1).Search("X", Arg.Any<int>(), Arg.Any<string?>(),
            Arg.Is<string?>(p => p == expectedAbsolute));
    }

    [Fact]
    public async Task ExecuteAsync_NotYetIndexed_ReturnsHelpfulMessage()
    {
        var svc = Substitute.For<ICodeIndexService>();
        svc.LastIndexedAt.Returns((DateTimeOffset?)null);
        svc.IsIndexing.Returns(false);

        var sut = new SymbolSearchTool(svc, Substitute.For<IWorkingDirectoryAccessor>(), Substitute.For<ILspServerManager>());
        var result = await sut.SymbolSearchAsync("Foo");

        result.Content.Should().Contain("still building");
    }
}
