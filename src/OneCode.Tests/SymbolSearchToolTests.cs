using System.Text.Json;
using OneCode.Core.Lsp;
using OneCode.Core.Tools;

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

    // S1: LSP 成功路径也必须遵守 path 作用域

    /// <summary>
    /// 反证：修复前 path 只在索引回退路径生效，LSP 成功时直接返回全部命中。
    /// 这条用例在 LSP 分支漏掉 path 过滤时失败。
    /// </summary>
    [Fact]
    public async Task SymbolSearchAsync_LspHits_AreFilteredByPathScope()
    {
        var inScope = Path.Combine(_tmpDir, "src");
        var outOfScope = Path.Combine(_tmpDir, "other");

        var servers = Substitute.For<ILspServerManager>();
        servers.GetStatus().Returns([new LspServerStatus { Name = "csharp", IsInitialized = true, IsRunning = true }]);
        servers.SendRequestAsync("csharp", "workspace/symbol", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.SerializeToElement(new object[]
            {
                SymbolInfo("InScope", "class", Path.Combine(inScope, "A.cs")),
                SymbolInfo("OutOfScope", "class", Path.Combine(outOfScope, "B.cs")),
            }));

        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(_tmpDir);
        var sut = new SymbolSearchTool(Substitute.For<ICodeIndexService>(), wd, servers);

        var result = await sut.SymbolSearchAsync("Scope", path: "src");

        result.Content.Should().Contain("InScope");
        result.Content.Should().NotContain("OutOfScope",
            "the LSP path must honour the caller's path scope, not only the index fallback");
    }

    /// <summary>调用方取消必须穿透 LSP 请求，而不是被当成「服务器失败」后回退到索引。</summary>
    [Fact]
    public async Task SymbolSearchAsync_CallerCancelled_PropagatesInsteadOfFallingBack()
    {
        var servers = Substitute.For<ILspServerManager>();
        servers.GetStatus().Returns([new LspServerStatus { Name = "csharp", IsInitialized = true, IsRunning = true }]);
        servers.SendRequestAsync("csharp", "workspace/symbol", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns<JsonElement?>(_ => throw new OperationCanceledException());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var sut = new SymbolSearchTool(
            Substitute.For<ICodeIndexService>(), Substitute.For<IWorkingDirectoryAccessor>(), servers);

        var act = () => sut.SymbolSearchAsync("Scope", ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static object SymbolInfo(string name, string kind, string filePath) => new
    {
        name,
        kind = KindNumber(kind),
        location = new
        {
            uri = new Uri(filePath).AbsoluteUri,
            range = new { start = new { line = 0, character = 0 } },
        },
    };

    private static int KindNumber(string kind) => kind switch
    {
        "class" => 5,
        _ => 0,
    };
}
