using System.Text.Json;
using OneCode.Infrastructure.Lsp;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.Infrastructure.Search;
using OneCode.App.Tools;
using OneCode.Core.IO;
using OneCode.Core.Lsp;
using OneCode.Core.Tools;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Config;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="LspTool"/> — covers server-status gating, action→LSP method
/// routing with response pass-through, server auto-resolution fallback, capability gating,
/// completion projection/truncation, the 1-based → 0-based line/column conversion, default-newName
/// handling, workspaceSymbol index fallback, and symbol-based references text-search fallback.
/// </summary>
public sealed class LspToolTests
{
    private static ILspServerManager CreateManagerWithServer(string serverName = "test-server")
    {
        var manager = Substitute.For<ILspServerManager>();
        manager.GetStatus().Returns(new List<LspServerStatus>
        {
            new() { Name = serverName, IsRunning = true, IsInitialized = true },
        });
        manager.SendRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<JsonElement?>(null));
        return manager;
    }

    private static LanguagePackRegistry CreatePackRegistry() =>
        new(NullLogger<LanguagePackRegistry>.Instance);

    /// <summary>Full-ctor sut：默认给 substitute 依赖，测试只关注传入的那几个。</summary>
    private static LspTool CreateSut(
        ILspServerManager manager,
        ICodeIndexService? index = null,
        ITextSearchService? textSearch = null,
        IWorkingDirectoryAccessor? wd = null)
    {
        var workingDir = wd ?? Substitute.For<IWorkingDirectoryAccessor>();
        return new LspTool(
            manager, CreatePackRegistry(),
            index ?? Substitute.For<ICodeIndexService>(),
            textSearch ?? Substitute.For<ITextSearchService>(),
            workingDir);
    }

    [Fact]
    public async Task ExecuteLspAsync_NoServersRunning_ReturnsErrorJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = Substitute.For<ILspServerManager>();
        manager.GetStatus().Returns(new List<LspServerStatus>());
        var sut = CreateSut(manager);

        var result = await sut.ExecuteLspAsync("definition", "test.cs", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("No LSP servers running");
    }

    [Fact]
    public async Task ExecuteLspAsync_UnknownAction_ReturnsErrorJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        var sut = CreateSut(manager);

        var result = await sut.ExecuteLspAsync("notARealAction", "test.cs", server: "test-server", ct: ct);

        // Unknown action is reported inside a Success payload (an "error" field), not as IsError.
        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Unknown LSP action: notARealAction");
        // No LSP request should have been dispatched for an unknown action.
        await manager.DidNotReceive().SendRequestAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    // Action routing: every action must dispatch to its own LSP method, and the server
    // response must reach the caller unchanged. Action names are deliberately mixed-case
    // to lock the case-insensitive lookup.

    [Theory]
    [InlineData("definition", "textDocument/definition")]
    [InlineData("declaration", "textDocument/declaration")]
    [InlineData("typeDefinition", "textDocument/typeDefinition")]
    [InlineData("implementation", "textDocument/implementation")]
    [InlineData("references", "textDocument/references")]
    [InlineData("hover", "textDocument/hover")]
    [InlineData("documentHighlight", "textDocument/documentHighlight")]
    [InlineData("symbols", "textDocument/documentSymbol")]
    [InlineData("codeAction", "textDocument/codeAction")]
    [InlineData("formatting", "textDocument/formatting")]
    [InlineData("signatureHelp", "textDocument/signatureHelp")]
    public async Task ExecuteLspAsync_Action_DispatchesExpectedMethodAndPassesResponseThrough(string action, string expectedMethod)
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        using var responseDoc = JsonDocument.Parse("""{"payload":"from-server"}""");
        manager.SendRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<JsonElement?>(responseDoc.RootElement.Clone()));
        var sut = CreateSut(manager);

        var result = await sut.ExecuteLspAsync(action, "test.cs", line: 5, column: 10, server: "test-server", ct: ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("from-server", "the server response must pass through unchanged");
        await manager.Received(1).SendRequestAsync(
            "test-server", expectedMethod, Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    // Completion: the raw LSP items are projected to compact entries and truncated.

    [Fact]
    public async Task ExecuteLspAsync_CompletionAction_ProjectsCompactItemsAndReportsTotal()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        using var responseDoc = JsonDocument.Parse(
            """[{"label":"FirstSymbol","kind":3,"detail":"int","documentation":"dropped"},{"label":"SecondSymbol","kind":6}]""");
        manager.SendRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<JsonElement?>(responseDoc.RootElement.Clone()));
        var sut = CreateSut(manager);

        var result = await sut.ExecuteLspAsync("completion", "test.cs", server: "test-server", ct: ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("\"total\":2");
        result.Content.Should().Contain("\"returned\":2");
        result.Content.Should().Contain("FirstSymbol");
        result.Content.Should().Contain("SecondSymbol");
        // Editor-only noise is projected away to keep the payload small for the agent.
        result.Content.Should().NotContain("dropped");
        await manager.Received(1).SendRequestAsync(
            "test-server", "textDocument/completion", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteLspAsync_CompletionAction_TruncatesToMaxItems()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        var itemsJson = "[" + string.Join(",", Enumerable.Range(0, 30).Select(i => $$"""{"label":"item{{i}}"}""")) + "]";
        using var responseDoc = JsonDocument.Parse(itemsJson);
        manager.SendRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<JsonElement?>(responseDoc.RootElement.Clone()));
        var sut = CreateSut(manager);

        var result = await sut.ExecuteLspAsync("completion", "test.cs", server: "test-server", ct: ct);

        result.Content.Should().Contain("\"total\":30");
        result.Content.Should().Contain("\"returned\":25");
        result.Content.Should().Contain("item24");
        result.Content.Should().NotContain("item25");
    }

    [Fact]
    public async Task ExecuteLspAsync_ExecuteCommandAction_PassesServerOutputThrough()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        using var responseDoc = JsonDocument.Parse("""{"output":"command-output"}""");
        manager.SendRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<JsonElement?>(responseDoc.RootElement.Clone()));
        var sut = CreateSut(manager);

        var result = await sut.ExecuteLspAsync("executeCommand", file: "", server: "test-server", query: "my.command", ct: ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("command-output", "the command result must reach the caller");
        await manager.Received(1).SendRequestAsync(
            "test-server", "workspace/executeCommand", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteLspAsync_ExecuteCommandWithoutQuery_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        var sut = CreateSut(manager);

        var result = await sut.ExecuteLspAsync("executeCommand", file: "", server: "test-server", query: null, ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("executeCommand requires the 'query' parameter");
    }

    // Capability gating

    [Fact]
    public async Task ExecuteLspAsync_CapabilityNotSupported_ReturnsErrorJson()
    {
        var ct = TestContext.Current.CancellationToken;
        // Server advertises hover but NOT declaration — capability gate must reject.
        using var doc = JsonDocument.Parse("""{"textDocument":{"hover":{}}}""");
        var caps = doc.RootElement.Clone();
        var manager = Substitute.For<ILspServerManager>();
        manager.GetStatus().Returns(new List<LspServerStatus>
        {
            new() { Name = "test-server", IsRunning = true, IsInitialized = true, Capabilities = caps },
        });
        var sut = CreateSut(manager);

        var result = await sut.ExecuteLspAsync("declaration", "test.cs", server: "test-server", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("does not support");
        await manager.DidNotReceive().SendRequestAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    // Diagnostics action (no SendRequestAsync — uses GetDiagnostics)

    [Fact]
    public async Task ExecuteLspAsync_DiagnosticsAction_ReturnsServerDiagnostics()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        manager.GetDiagnostics("test-server").Returns(new List<LspDiagnosticEntry>
        {
            new()
            {
                ServerName = "test-server",
                Severity = LspDiagnosticSeverity.Error,
                Message = "CS1002: ; expected",
                Timestamp = DateTimeOffset.UtcNow,
                File = "test.cs",
                Line = 5,
                Column = 10,
            },
        });
        var sut = CreateSut(manager);

        var result = await sut.ExecuteLspAsync("diagnostics", "test.cs", server: "test-server", ct: ct);

        result.Content.Should().Contain("CS1002");
        result.Content.Should().Contain("\"server\":\"test-server\"");
        // Diagnostics must NOT dispatch a JSON-RPC request — it reads from the local registry.
        await manager.DidNotReceive().SendRequestAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    // Rename action: default newName

    [Fact]
    public async Task ExecuteLspAsync_RenameWithoutNewName_UsesDefaultNewName()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        JsonElement capturedParams = default;
        manager.SendRequestAsync("test-server", "textDocument/rename", Arg.Do<JsonElement>(p => capturedParams = p), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<JsonElement?>(null));
        var sut = CreateSut(manager);

        await sut.ExecuteLspAsync("rename", "test.cs", line: 3, column: 7, server: "test-server", newName: null, ct: ct);

        // The tool must fall back to the literal "newName" when newName is null,
        // rather than sending a null/empty value to the LSP server.
        capturedParams.GetProperty("newName").GetString().Should().Be("newName");
    }

    [Fact]
    public async Task ExecuteLspAsync_RenameWithNewName_PassesNewNameToServer()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        JsonElement capturedParams = default;
        manager.SendRequestAsync("test-server", "textDocument/rename", Arg.Do<JsonElement>(p => capturedParams = p), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<JsonElement?>(null));
        var sut = CreateSut(manager);

        await sut.ExecuteLspAsync("rename", "test.cs", line: 3, column: 7, server: "test-server", newName: "RenamedSymbol", ct: ct);

        capturedParams.GetProperty("newName").GetString().Should().Be("RenamedSymbol");
    }

    // 1-based → 0-based line/column conversion

    [Fact]
    public async Task ExecuteLspAsync_PositionParams_ConvertLineColumnToZeroBased()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        JsonElement capturedParams = default;
        manager.SendRequestAsync("test-server", "textDocument/definition", Arg.Do<JsonElement>(p => capturedParams = p), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<JsonElement?>(null));
        var sut = CreateSut(manager);

        // User passes 1-based line=5, column=10 → LSP expects 0-based line=4, character=9.
        await sut.ExecuteLspAsync("definition", "test.cs", line: 5, column: 10, server: "test-server", ct: ct);

        var position = capturedParams.GetProperty("position");
        position.GetProperty("line").GetInt32().Should().Be(4);
        position.GetProperty("character").GetInt32().Should().Be(9);
    }

    // Server auto-resolution fallback

    [Fact]
    public async Task ExecuteLspAsync_NoServerSpecified_FallsBackToFirstRunningServer()
    {
        var ct = TestContext.Current.CancellationToken;
        // Use an extension no built-in/user pack handles, so ResolveServerName returns null
        // and the tool falls back to status.FirstOrDefault().Name.
        var manager = Substitute.For<ILspServerManager>();
        manager.GetStatus().Returns(new List<LspServerStatus>
        {
            new() { Name = "fallback-server", IsRunning = true, IsInitialized = true },
        });
        using var responseDoc = JsonDocument.Parse("""{"payload":"fallback-response"}""");
        manager.SendRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<JsonElement?>(responseDoc.RootElement.Clone()));
        var sut = CreateSut(manager);

        var result = await sut.ExecuteLspAsync("definition", "file.unknownext", ct: ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("fallback-response");
        await manager.Received(1).SendRequestAsync(
            "fallback-server", "textDocument/definition", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    // Exception handling

    [Fact]
    public async Task ExecuteLspAsync_ServerThrows_ReturnsErrorJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        manager.SendRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns<Task<JsonElement?>>(_ => throw new InvalidOperationException("server crashed"));
        var sut = CreateSut(manager);

        var result = await sut.ExecuteLspAsync("definition", "test.cs", server: "test-server", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("LSP definition failed");
        result.Content.Should().Contain("server crashed");
    }

    [Fact]
    public async Task ExecuteLspAsync_FileOutsideWorkingDirectory_ReturnsErrorWithoutDispatchingRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        wd.WorkingDirectory.Returns(Path.Combine(Path.GetTempPath(), "LspWorkspace"));
        var sut = CreateSut(manager, wd: wd);

        var result = await sut.ExecuteLspAsync("definition", "../../outside/secret.cs", server: "test-server", ct: ct);

        result.IsError.Should().BeTrue("LspTool must enforce workspace path boundaries via SafeResolve");
        result.Content.Should().Contain("outside");
        await manager.DidNotReceive().SendRequestAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    // workspaceSymbol：索引回退

    [Fact]
    public async Task ExecuteLspAsync_WorkspaceSymbol_NoLspHitAndNullIndex_ReturnsUnavailableError()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = Substitute.For<ILspServerManager>();
        manager.GetStatus().Returns(new List<LspServerStatus>());
        var wd = Substitute.For<IWorkingDirectoryAccessor>();
        // index 明确为 null（未配置），配合无 LSP 服务器的 manager 触发"不可用"错误
        var sut = new LspTool(manager, CreatePackRegistry(), (ICodeIndexService)null!,
            Substitute.For<ITextSearchService>(), wd);

        var result = await sut.ExecuteLspAsync("workspaceSymbol", file: "", query: "Foo", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("unavailable");
    }

    [Fact]
    public async Task ExecuteLspAsync_WorkspaceSymbol_EmptyQuery_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut(CreateManagerWithServer());

        var result = await sut.ExecuteLspAsync("workspaceSymbol", file: "", query: "", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("requires the 'query' parameter");
    }

    [Fact]
    public async Task ExecuteLspAsync_WorkspaceSymbol_IndexHasNoResults_ReturnsNotFoundMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var index = Substitute.For<ICodeIndexService>();
        index.LastIndexedAt.Returns(DateTimeOffset.UtcNow);
        index.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns([]);
        var manager = Substitute.For<ILspServerManager>();
        manager.GetStatus().Returns(new List<LspServerStatus>());
        var sut = CreateSut(manager, index);

        var result = await sut.ExecuteLspAsync("workspaceSymbol", file: "", query: "NonExistent", ct: ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("No symbols found matching");
        result.Content.Should().Contain("NonExistent");
    }

    [Fact]
    public async Task ExecuteLspAsync_WorkspaceSymbol_IndexHits_ReturnJsonWithSummaryAndResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var symbol = new CodeSymbol("MyClass", "class", "/src/MyClass.cs", 5, 1);
        var index = Substitute.For<ICodeIndexService>();
        index.LastIndexedAt.Returns(DateTimeOffset.UtcNow);
        index.Search("MyClass", Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns([new CodeSymbolMatch(symbol, 1.0)]);
        var manager = Substitute.For<ILspServerManager>();
        manager.GetStatus().Returns(new List<LspServerStatus>());
        var sut = CreateSut(manager, index);

        var result = await sut.ExecuteLspAsync("workspaceSymbol", file: "", query: "MyClass", ct: ct);

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
    public async Task ExecuteLspAsync_WorkspaceSymbol_IndexFallback_UsesResultCap()
    {
        var ct = TestContext.Current.CancellationToken;
        var index = Substitute.For<ICodeIndexService>();
        index.LastIndexedAt.Returns(DateTimeOffset.UtcNow);
        index.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns([]);
        var manager = Substitute.For<ILspServerManager>();
        manager.GetStatus().Returns(new List<LspServerStatus>());
        var sut = CreateSut(manager, index);

        var result = await sut.ExecuteLspAsync("workspaceSymbol", file: "", query: "X", ct: ct);

        result.IsError.Should().BeFalse();
        index.Received(1).Search("X", Constants.Lsp.MaxResultsUpper, Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ExecuteLspAsync_WorkspaceSymbol_RelativePath_ResolvedToAbsolute()
    {
        var ct = TestContext.Current.CancellationToken;
        var tmpDir = Path.Combine(Path.GetTempPath(), "LspTool_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        try
        {
            var index = Substitute.For<ICodeIndexService>();
            index.LastIndexedAt.Returns(DateTimeOffset.UtcNow);
            index.Search(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns([]);
            var wd = Substitute.For<IWorkingDirectoryAccessor>();
            wd.WorkingDirectory.Returns(tmpDir);
            var manager = Substitute.For<ILspServerManager>();
            manager.GetStatus().Returns(new List<LspServerStatus>());
            var sut = CreateSut(manager, index, wd: wd);

            var result = await sut.ExecuteLspAsync("workspaceSymbol", file: "", query: "X", path: "src", ct: ct);

            result.IsError.Should().BeFalse();
            var expectedAbsolute = Path.GetFullPath(Path.Combine(tmpDir, "src"));
            index.Received(1).Search("X", Arg.Any<int>(), Arg.Any<string?>(),
                Arg.Is<string?>(p => p == expectedAbsolute));
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task ExecuteLspAsync_WorkspaceSymbol_IndexNotBuiltYet_ReturnsHelpfulMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var index = Substitute.For<ICodeIndexService>();
        index.LastIndexedAt.Returns((DateTimeOffset?)null);
        index.IsIndexing.Returns(false);
        var manager = Substitute.For<ILspServerManager>();
        manager.GetStatus().Returns(new List<LspServerStatus>());
        var sut = CreateSut(manager, index);

        var result = await sut.ExecuteLspAsync("workspaceSymbol", file: "", query: "Foo", ct: ct);

        result.Content.Should().Contain("still building");
    }

    /// <summary>
    /// 反证：path 作用域必须同样作用于 LSP 命中（修复前只有索引回退路径会过滤）。
    /// </summary>
    [Fact]
    public async Task ExecuteLspAsync_WorkspaceSymbol_LspHits_AreFilteredByPathScope()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "LspTool_" + Guid.NewGuid().ToString("N")[..8]);
        var inScope = Path.Combine(tmpDir, "src");
        var outOfScope = Path.Combine(tmpDir, "other");
        Directory.CreateDirectory(inScope);
        Directory.CreateDirectory(outOfScope);
        try
        {
            var servers = Substitute.For<ILspServerManager>();
            servers.GetStatus().Returns([new LspServerStatus { Name = "csharp", IsInitialized = true, IsRunning = true }]);
            servers.SendRequestAsync("csharp", "workspace/symbol", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
                .Returns(JsonSerializer.SerializeToElement(new object[]
                {
                    SymbolInfo("InScope", "class", Path.Combine(inScope, "A.cs")),
                    SymbolInfo("OutOfScope", "class", Path.Combine(outOfScope, "B.cs")),
                }));

            var wd = Substitute.For<IWorkingDirectoryAccessor>();
            wd.WorkingDirectory.Returns(tmpDir);
            var sut = CreateSut(servers, wd: wd);

            var result = await sut.ExecuteLspAsync("workspaceSymbol", file: "", query: "Scope", path: "src", ct: TestContext.Current.CancellationToken);

            result.Content.Should().Contain("InScope");
            result.Content.Should().NotContain("OutOfScope",
                "the LSP path must honour the caller's path scope, not only the index fallback");
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>调用方取消必须穿透 LSP 请求，而不是被当成「服务器失败」后回退到索引。</summary>
    [Fact]
    public async Task ExecuteLspAsync_WorkspaceSymbol_CallerCancelled_PropagatesInsteadOfFallingBack()
    {
        var servers = Substitute.For<ILspServerManager>();
        servers.GetStatus().Returns([new LspServerStatus { Name = "csharp", IsInitialized = true, IsRunning = true }]);
        servers.SendRequestAsync("csharp", "workspace/symbol", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns<JsonElement?>(_ => throw new OperationCanceledException());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var sut = CreateSut(servers);

        var act = () => sut.ExecuteLspAsync("workspaceSymbol", file: "", query: "Scope", ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // symbol-based references：LSP 不可用时退回词边界文本搜索

    [Fact]
    public async Task ExecuteLspAsync_ReferencesBySymbol_WithoutFileOrSymbol_ReturnsParameterError()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut(CreateManagerWithServer());

        var result = await sut.ExecuteLspAsync("references", file: "", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("'symbol'");
    }

    [Fact]
    public async Task ExecuteLspAsync_ReferencesBySymbol_InsideWorkingDir_FindsReferences()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sandbox = new ReferenceSandbox();
        sandbox.WriteFile("src/svc.cs", "var x = MyService.Get();");
        var sut = sandbox.CreateNativeTool();

        var result = await sut.ExecuteLspAsync("references", file: "", symbol: "MyService", path: "src", ct: ct);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("svc.cs");
        result.Content.Should().Contain("MyService");
    }

    [Fact]
    public async Task ExecuteLspAsync_ReferencesBySymbol_ExactWord_DoesNotMatchSubstringOccurrence()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sandbox = new ReferenceSandbox();
        // "MyServiceHelper" contains "MyService" as a substring but not as a whole word
        sandbox.WriteFile("src/sub.cs", "var x = MyServiceHelper.Create();");
        var sut = sandbox.CreateNativeTool();

        var result = await sut.ExecuteLspAsync("references", file: "", symbol: "MyService", path: "src", exact_word: true, ct: ct);

        result.Content.Should().Contain("No references found");
    }

    [Fact]
    public async Task ExecuteLspAsync_ReferencesBySymbol_ExactWordFalse_MatchesSubstring()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sandbox = new ReferenceSandbox();
        sandbox.WriteFile("src/sub.cs", "var x = MyServiceHelper.Create();");
        var sut = sandbox.CreateNativeTool();

        var result = await sut.ExecuteLspAsync("references", file: "", symbol: "MyService", path: "src", exact_word: false, ct: ct);

        result.Content.Should().Contain("sub.cs");
        result.Content.Should().Contain("MyServiceHelper");
    }

    [Fact]
    public async Task ExecuteLspAsync_ReferencesBySymbol_NoResults_ReturnsNotFoundMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sandbox = new ReferenceSandbox();
        sandbox.WriteFile("src/empty.cs", "var x = 42;");
        var sut = sandbox.CreateNativeTool();

        var result = await sut.ExecuteLspAsync("references", file: "", symbol: "NonExistentSymbol", path: "src", ct: ct);

        result.Content.Should().Contain("No references found");
        result.Content.Should().Contain("NonExistentSymbol");
    }

    [Fact]
    public async Task ExecuteLspAsync_ReferencesBySymbol_ExcludeGlob_ExcludesMatchingFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sandbox = new ReferenceSandbox();
        Directory.CreateDirectory(Path.Combine(sandbox.ProjectDir, "src", "tests"));
        sandbox.WriteFile("src/main.cs", "var x = Target.Get();");
        sandbox.WriteFile("src/tests/test.cs", "var x = Target.Get();");
        var sut = sandbox.CreateNativeTool();

        var result = await sut.ExecuteLspAsync("references", file: "", symbol: "Target", path: "src", exclude_glob: "tests/**", ct: ct);

        result.Content.Should().Contain("main.cs");
        result.Content.Should().NotContain("test.cs");
    }

    [Fact]
    public async Task ExecuteLspAsync_ReferencesBySymbol_MaxResults_TruncatesAndReportsTotal()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sandbox = new ReferenceSandbox();
        for (var i = 0; i < 20; i++)
            sandbox.WriteFile($"src/f{i}.cs", $"var x = TargetSym.M{i}();");
        var sut = sandbox.CreateNativeTool();

        var result = await sut.ExecuteLspAsync("references", file: "", symbol: "TargetSym", path: "src", max_results: 5, ct: ct);

        result.Content.Should().Contain("Showing first 5 of 20");
    }

    [Fact]
    public async Task ExecuteLspAsync_ReferencesBySymbol_MultipleFiles_AllReferencesReported()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sandbox = new ReferenceSandbox();
        sandbox.WriteFile("src/a.cs", "var a = SharedSymbol();");
        sandbox.WriteFile("src/b.cs", "var b = SharedSymbol();");
        sandbox.WriteFile("src/c.cs", "var c = SharedSymbol();");
        var sut = sandbox.CreateNativeTool();

        var result = await sut.ExecuteLspAsync("references", file: "", symbol: "SharedSymbol", path: "src", ct: ct);

        result.Content.Should().Contain("Found 3 references");
        result.Content.Should().Contain("a.cs");
        result.Content.Should().Contain("b.cs");
        result.Content.Should().Contain("c.cs");
    }

    [Fact]
    public async Task ExecuteLspAsync_ReferencesBySymbol_RipgrepAvailable_DelegatesToRgAndStripsPathPrefix()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sandbox = new ReferenceSandbox();
        sandbox.WriteFile("src/rg.cs", "var x = MySymbol();");
        var searchPath = Path.Combine(sandbox.ProjectDir, "src");
        var prefix = searchPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var processRunner = Substitute.For<IProcessRunner>();
        processRunner.CommandExistsAsync("rg").Returns(true);
        // rg 以工作区根为运行目录，此处模拟返回绝对 searchPath 前缀的结果。
        processRunner.ExecuteAsync("rg", Arg.Any<string[]>(), sandbox.ProjectDir, ct: Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, prefix + "rg.cs:1:var x = MySymbol();", "", false));

        var sut = sandbox.CreateNativeTool(processRunner);

        var result = await sut.ExecuteLspAsync("references", file: "", symbol: "MySymbol", path: "src", ct: ct);

        result.Content.Should().Contain("rg.cs:1:");
        result.Content.Should().NotContain(prefix);
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

    /// <summary>
    /// references 回退路径测试的沙盒：临时目录 + 原生（无 ripgrep）文本搜索。
    /// LSP 侧传入空状态管理器，强制工具走文本搜索回退。
    /// </summary>
    private sealed class ReferenceSandbox : IDisposable
    {
        public ReferenceSandbox()
        {
            SandboxDir = Path.Combine(Path.GetTempPath(), $"LspRefTests_{Guid.NewGuid():N}");
            ProjectDir = Path.Combine(SandboxDir, "project");
            Directory.CreateDirectory(ProjectDir);
            Directory.CreateDirectory(Path.Combine(ProjectDir, "src"));
        }

        public string SandboxDir { get; }
        public string ProjectDir { get; }

        public void WriteFile(string relativeName, string content)
        {
            var path = Path.Combine(ProjectDir, relativeName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        /// <summary>创建使用原生 C# 回退（无 ripgrep）与真实 <see cref="LocalAgentFileStore"/> 的工具。</summary>
        public LspTool CreateNativeTool(IProcessRunner? processRunner = null)
        {
            var runner = processRunner ?? Substitute.For<IProcessRunner>();
            if (processRunner is null)
                runner.CommandExistsAsync("rg").Returns(false);
            var wd = Substitute.For<IWorkingDirectoryAccessor>();
            wd.WorkingDirectory.Returns(ProjectDir);
            var fileStore = new LocalAgentFileStore(wd);
            var textSearch = new TextSearchService(runner, fileStore, NullLogger<TextSearchService>.Instance);
            var manager = Substitute.For<ILspServerManager>();
            manager.GetStatus().Returns(new List<LspServerStatus>());
            return new LspTool(manager, CreatePackRegistry(), Substitute.For<ICodeIndexService>(), textSearch, wd);
        }

        public void Dispose()
        {
            try { Directory.Delete(SandboxDir, recursive: true); } catch { /* best effort */ }
        }
    }
}