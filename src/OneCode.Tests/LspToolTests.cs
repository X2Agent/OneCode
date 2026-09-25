using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OneCode.App.Services.Lsp;
using OneCode.App.Tools;
using OneCode.Core.Lsp;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="LspTool"/> — covers server-status gating, action→LSP method
/// routing with response pass-through, server auto-resolution fallback, capability gating,
/// completion projection/truncation, the 1-based → 0-based line/column conversion, and
/// default-newName handling.
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

    [Fact]
    public async Task ExecuteLspAsync_UnknownAction_ReturnsErrorJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = CreateManagerWithServer();
        var sut = new LspTool(manager, CreatePackRegistry());

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
        var sut = new LspTool(manager, CreatePackRegistry());

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
        var sut = new LspTool(manager, CreatePackRegistry());

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
        var sut = new LspTool(manager, CreatePackRegistry());

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
        var sut = new LspTool(manager, CreatePackRegistry());

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
        using var responseDoc = JsonDocument.Parse("""{"payload":"fallback-response"}""");
        manager.SendRequestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<JsonElement?>(responseDoc.RootElement.Clone()));
        var sut = new LspTool(manager, CreatePackRegistry());

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
        var sut = new LspTool(manager, CreatePackRegistry());

        var result = await sut.ExecuteLspAsync("definition", "test.cs", server: "test-server", ct: ct);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("LSP definition failed");
        result.Content.Should().Contain("server crashed");
    }
}
