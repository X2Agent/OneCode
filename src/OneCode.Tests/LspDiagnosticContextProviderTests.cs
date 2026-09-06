using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OneCode.App.Services.Context;
using OneCode.App.Services.Lsp;
using OneCode.Infrastructure.Text;
using System.Reflection;

namespace OneCode.Tests;

/// <summary>
/// Unit tests for <see cref="LspDiagnosticContextProvider"/> — Layer 3 LSP context
/// injection: no-op when there are no Error/Warning diagnostics under the working
/// directory, file/severity summary injection otherwise.
/// Uses reflection to invoke the protected ProvideAIContextAsync method
/// (same pattern as <see cref="TaskContextProviderTests"/>).
/// </summary>
public sealed class LspDiagnosticContextProviderTests : IDisposable
{
    private readonly string _workingDirectory;

    public LspDiagnosticContextProviderTests()
    {
        _workingDirectory = Path.Combine(
            Path.GetTempPath(), "onecode-lsp-diag-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workingDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workingDirectory, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Invokes the protected ProvideAIContextAsync via reflection.
    /// The method doesn't use the InvokingContext parameter, so an uninitialized instance suffices.
    /// </summary>
    private static async Task<AIContext> InvokeProvideAIContext(LspDiagnosticContextProvider provider)
    {
        var method = typeof(LspDiagnosticContextProvider).GetMethod(
            "ProvideAIContextAsync",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.InvokeMethod);

        var contextType = typeof(AIContextProvider).GetNestedType(
            "InvokingContext",
            BindingFlags.NonPublic | BindingFlags.Public)!;
        var context = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(contextType);

        var result = (ValueTask<AIContext>)method!.Invoke(provider, [context, CancellationToken.None])!;
        return await result.AsTask();
    }

    private static LspDiagnosticContextProvider CreateProviderFor(LspDiagnosticRegistry registry, string workingDirectory)
        => new(registry, NullLogger<LspDiagnosticContextProvider>.Instance, workingDirectory);

    /// <summary>
    /// Build a textDocument/publishDiagnostics notification with a single diagnostic,
    /// mirroring the JSON shape parsed by <see cref="LspDiagnosticRegistry.ProcessDiagnostics"/>:
    /// severity follows the LSP wire enum (1=Error, 2=Warning, 3=Information, 4=Hint).
    /// </summary>
    private static JsonElement CreatePublishParams(string uri, int severity, string message, int startLine, string source)
        => JsonSerializer.SerializeToElement(new
        {
            uri,
            diagnostics = new object[]
            {
                new
                {
                    severity,
                    message,
                    source,
                    range = new
                    {
                        start = new { line = startLine, character = 0 },
                        @end = new { line = startLine, character = 5 },
                    },
                },
            },
        });

    private string FileUriInSandbox(string fileName)
    {
        var filePath = Path.Combine(_workingDirectory, fileName);
        File.WriteAllText(filePath, "// placeholder so the path plausibly exists");
        return LspUriHelper.BuildFileUri(filePath);
    }

    // No diagnostics → no injection

    [Fact]
    public async Task ProvideAIContextAsync_NoDiagnostics_ReturnsEmptyContext()
    {
        var registry = new LspDiagnosticRegistry();
        var provider = CreateProviderFor(registry, _workingDirectory);

        var context = await InvokeProvideAIContext(provider);

        (context.Messages ?? []).Should().BeEmpty();
    }

    // Severity filtering: Information/Hint are intentionally excluded

    [Fact]
    public async Task ProvideAIContextAsync_OnlyInformationAndHintDiagnostics_ReturnsEmptyContext()
    {
        var registry = new LspDiagnosticRegistry();
        var uri = FileUriInSandbox("Info.cs");
        registry.ProcessDiagnostics("roslyn", CreatePublishParams(uri, severity: 3, "info message", startLine: 0, source: "csc"));
        registry.ProcessDiagnostics("roslyn", CreatePublishParams(uri, severity: 4, "hint message", startLine: 1, source: "csc"));
        var provider = CreateProviderFor(registry, _workingDirectory);

        var context = await InvokeProvideAIContext(provider);

        (context.Messages ?? []).Should().BeEmpty();
    }

    // File under working directory → injected with file/severity/line summary

    [Fact]
    public async Task ProvideAIContextAsync_ErrorUnderWorkingDirectory_InjectsSystemMessage()
    {
        var registry = new LspDiagnosticRegistry();
        var uri = FileUriInSandbox("Code.cs");
        registry.ProcessDiagnostics("roslyn", CreatePublishParams(uri, severity: 1, "CS1002: ; expected", startLine: 1, source: "csc"));
        var provider = CreateProviderFor(registry, _workingDirectory);

        var context = await InvokeProvideAIContext(provider);

        context.Messages.Should().HaveCount(1);
        var msg = context.Messages.First();
        msg.Role.Should().Be(ChatRole.System);
        msg.Text.Should().Contain("Code.cs");
        msg.Text.Should().Contain("ERROR");
        msg.Text.Should().Contain("L2"); // 0-based line 1 → 1-based L2
        msg.Text.Should().Contain("CS1002: ; expected");
        msg.Text.Should().Contain("csc");
    }

    [Fact]
    public async Task ProvideAIContextAsync_WarningUnderWorkingDirectory_InjectedAsWarn()
    {
        var registry = new LspDiagnosticRegistry();
        var uri = FileUriInSandbox("Legacy.cs");
        registry.ProcessDiagnostics("roslyn", CreatePublishParams(uri, severity: 2, "CS0219: variable assigned but never used", startLine: 4, source: "csc"));
        var provider = CreateProviderFor(registry, _workingDirectory);

        var context = await InvokeProvideAIContext(provider);

        context.Messages.Should().HaveCount(1);
        var msg = context.Messages.First();
        msg.Role.Should().Be(ChatRole.System);
        msg.Text.Should().Contain("WARN");
        msg.Text.Should().Contain("Legacy.cs");
        msg.Text.Should().Contain("L5");
    }

    // Path boundary: diagnostics outside the working directory are not injected

    [Fact]
    public async Task ProvideAIContextAsync_FileOutsideWorkingDirectory_NotInjected()
    {
        var registry = new LspDiagnosticRegistry();
        var outsideUri = LspUriHelper.BuildFileUri(Path.Combine(Path.GetTempPath(), "onecode-outside-sandbox.cs"));
        registry.ProcessDiagnostics("roslyn", CreatePublishParams(outsideUri, severity: 1, "outside error", startLine: 0, source: "csc"));
        var provider = CreateProviderFor(registry, _workingDirectory);

        var context = await InvokeProvideAIContext(provider);

        (context.Messages ?? []).Should().BeEmpty();
    }
}
