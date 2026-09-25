using System.Text.Json;
using Microsoft.Extensions.AI;
using NSubstitute;
using OneCode.App.Services.Mcp;

namespace OneCode.Tests;

public sealed class McpToolNamingTests
{
    [Fact]
    public void CreateUniqueToolName_NormalizesInvalidCharactersAndLength()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var name = McpConnectionManager.CreateUniqueToolName(
            "agent.mail/prod", new string('x', 100), used);

        name.Should().MatchRegex("^[A-Za-z0-9_-]+$");
        name.Should().StartWith("mcp__agent_mail_prod__");
        name.Length.Should().BeLessThanOrEqualTo(64);
    }

    [Fact]
    public void CreateUniqueToolName_NormalizationCollisionGetsDeterministicSuffix()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var first = McpConnectionManager.CreateUniqueToolName("server", "list/items", used);
        var second = McpConnectionManager.CreateUniqueToolName("server", "list items", used);

        first.Should().Be("mcp__server__list_items");
        second.Should().Be("mcp__server__list_items__2");
    }

    [Fact]
    public async Task RenamedAIFunction_RenamesWrapperAndDelegatesEverythingElse()
    {
        using var inputSchema = JsonDocument.Parse("""{"type":"object","properties":{"value":{"type":"string"}}}""");
        var inner = Substitute.For<AIFunction>();
        inner.Name.Returns("original");
        inner.Description.Returns("description");
        inner.JsonSchema.Returns(inputSchema.RootElement.Clone());
        inner.InvokeAsync(default!, default)
            .ReturnsForAnyArgs(new ValueTask<object?>("ok"));

        var renamed = new RenamedAIFunction(inner, "mcp__server__original");
        var result = await renamed.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);

        // The wrapper's single responsibility: expose the new name while the inner
        // metadata and invocation pass through unchanged.
        renamed.Name.Should().Be("mcp__server__original");
        renamed.Description.Should().Be("description");
        renamed.JsonSchema.GetRawText().Should().Be(inner.JsonSchema.GetRawText());
        result.Should().Be("ok", "invocation must be delegated to the wrapped function");
    }
}
