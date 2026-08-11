using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Lsp;
using OneCode.App.Tools;
using OneCode.Core.Lsp;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="LspTool"/> — covers server-status gating, server
/// auto-resolution fallback, action routing (definition/declaration/diagnostics/rename/etc.),
/// capability gating, the 1-based → 0-based line/column conversion, and default-newName
/// handling.
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

    // Server-status gating

    [Fact]
    public async Task ExecuteLspAsync_NoServersRunning_ReturnsErrorJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = Substitute.For<ILspServerManager>();
        manager.GetStatus().Returns(new List<LspServerStatus>());
        var sut = new LspTool(manager, CreatePackRegistry());

        var result = await sut.ExecuteLspAsync("definition", "test.cs", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("No LSP servers running");
    }

    // Action routing

    [Fact]
    public async Task ExecuteLspAsync_UnknownAction_ReturnsErrorJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        var sut = new LspTool(manager, CreatePackRegistry());

        var result = await sut.ExecuteLspAsync("notARealAction", "test.cs", server: "test-server", ct: ct);

        // Unknown action is returned as a Success result with an "error" field in the
        // JSON payload (the tool wraps it via ToolResult.Success), so IsError is false.
        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Unknown LSP action: notARealAction");
        // No LSP request should have been dispatched for an unknown action.
        await manager.DidNotReceive().SendRequestAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteLspAsync_DefinitionAction_CallsTextDocumentDefinition()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        var sut = new LspTool(manager, CreatePackRegistry());

        var result = await sut.ExecuteLspAsync("definition", "test.cs", line: 5, column: 10, server: "test-server", ct: ct);

        // Business assertion: the default structure for a null server response is returned.
        result.Content.Should().Contain("locations");
        // Routing assertion: the correct LSP method was dispatched.
        await manager.Received(1).SendRequestAsync(
            "test-server", "textDocument/definition", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteLspAsync_DeclarationAction_CallsTextDocumentDeclaration()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        var sut = new LspTool(manager, CreatePackRegistry());

        var result = await sut.ExecuteLspAsync("declaration", "test.cs", line: 5, column: 10, server: "test-server", ct: ct);

        result.Content.Should().Contain("locations");
        await manager.Received(1).SendRequestAsync(
            "test-server", "textDocument/declaration", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteLspAsync_DocumentHighlightAction_CallsDocumentHighlight()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        var sut = new LspTool(manager, CreatePackRegistry());

        var result = await sut.ExecuteLspAsync("documentHighlight", "test.cs", server: "test-server", ct: ct);

        result.Content.Should().Contain("highlights");
        await manager.Received(1).SendRequestAsync(
            "test-server", "textDocument/documentHighlight", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteLspAsync_ReferencesAction_CallsTextDocumentReferences()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        var sut = new LspTool(manager, CreatePackRegistry());

        var result = await sut.ExecuteLspAsync("references", "test.cs", server: "test-server", ct: ct);

        result.Content.Should().Contain("references");
        await manager.Received(1).SendRequestAsync(
            "test-server", "textDocument/references", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteLspAsync_HoverAction_CallsTextDocumentHover()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        var sut = new LspTool(manager, CreatePackRegistry());

        var result = await sut.ExecuteLspAsync("hover", "test.cs", server: "test-server", ct: ct);

        result.Content.Should().Contain("contents");
        await manager.Received(1).SendRequestAsync(
            "test-server", "textDocument/hover", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteLspAsync_SymbolsAction_CallsDocumentSymbol()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        var sut = new LspTool(manager, CreatePackRegistry());

        var result = await sut.ExecuteLspAsync("symbols", "test.cs", server: "test-server", ct: ct);

        result.Content.Should().Contain("symbols");
        await manager.Received(1).SendRequestAsync(
            "test-server", "textDocument/documentSymbol", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteLspAsync_CompletionAction_CallsCompletion()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        var sut = new LspTool(manager, CreatePackRegistry());

        var result = await sut.ExecuteLspAsync("completion", "test.cs", server: "test-server", ct: ct);

        result.Content.Should().Contain("items");
        await manager.Received(1).SendRequestAsync(
            "test-server", "textDocument/completion", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteLspAsync_CodeAction_CallsCodeAction()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        var sut = new LspTool(manager, CreatePackRegistry());

        var result = await sut.ExecuteLspAsync("codeAction", "test.cs", server: "test-server", ct: ct);

        result.Content.Should().Contain("actions");
        await manager.Received(1).SendRequestAsync(
            "test-server", "textDocument/codeAction", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteLspAsync_FormattingAction_CallsFormatting()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        var sut = new LspTool(manager, CreatePackRegistry());

        var result = await sut.ExecuteLspAsync("formatting", "test.cs", server: "test-server", ct: ct);

        result.Content.Should().Contain("edits");
        await manager.Received(1).SendRequestAsync(
            "test-server", "textDocument/formatting", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteLspAsync_SignatureHelpAction_CallsSignatureHelp()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        var sut = new LspTool(manager, CreatePackRegistry());

        var result = await sut.ExecuteLspAsync("signatureHelp", "test.cs", server: "test-server", ct: ct);

        result.Content.Should().Contain("signatures");
        await manager.Received(1).SendRequestAsync(
            "test-server", "textDocument/signatureHelp", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    // Phase 3: executeCommand / hierarchy / semanticTokens / inlayHint

    [Fact]
    public async Task ExecuteLspAsync_ExecuteCommandAction_CallsWorkspaceExecuteCommand()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        var sut = new LspTool(manager, CreatePackRegistry());

        var result = await sut.ExecuteLspAsync("executeCommand", file: "", server: "test-server", query: "my.command", ct: ct);

        result.Content.Should().Contain("result");
        await manager.Received(1).SendRequestAsync(
            "test-server", "workspace/executeCommand", Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteLspAsync_ExecuteCommandWithoutQuery_ReturnsError()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        var sut = new LspTool(manager, CreatePackRegistry());

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
        var sut = new LspTool(manager, CreatePackRegistry());

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
        var sut = new LspTool(manager, CreatePackRegistry());

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
        var sut = new LspTool(manager, CreatePackRegistry());

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
        var sut = new LspTool(manager, CreatePackRegistry());

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
        var sut = new LspTool(manager, CreatePackRegistry());

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
        manager.SendRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<JsonElement?>(null));
        var sut = new LspTool(manager, CreatePackRegistry());

        await sut.ExecuteLspAsync("definition", "file.unknownext", ct: ct);

        await manager.Received(1).SendRequestAsync(
            "fallback-server", Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    // Exception handling

    [Fact]
    public async Task ExecuteLspAsync_ServerThrows_ReturnsErrorJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        manager.SendRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns<Task<JsonElement?>>(_ => throw new InvalidOperationException("server crashed"));
        var sut = new LspTool(manager, CreatePackRegistry());

        var result = await sut.ExecuteLspAsync("definition", "test.cs", server: "test-server", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("LSP definition failed");
        result.Content.Should().Contain("server crashed");
    }
}
