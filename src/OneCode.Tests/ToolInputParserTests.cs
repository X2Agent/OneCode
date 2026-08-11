using OneCode.App.Tui;

namespace OneCode.Tests;

public sealed class ToolInputParserTests
{
    [Fact]
    public void Parse_NullOrWhitespace_ReturnsEmptySuccess()
    {
        var result = ToolInputParser.Parse(null);
        result.Ok.Should().BeTrue();
        result.Error.Should().BeNull();
        result.Input.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Object);

        ToolInputParser.Parse("   ").Ok.Should().BeTrue();
    }

    [Fact]
    public void Parse_ValidJson_ReturnsElement()
    {
        var result = ToolInputParser.Parse("""{"command":"ls"}""");
        result.Ok.Should().BeTrue();
        result.Input.GetProperty("command").GetString().Should().Be("ls");
    }

    [Fact]
    public void Parse_InvalidJson_FailClosedWithPreview()
    {
        var result = ToolInputParser.Parse("{not-json");
        result.Ok.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        result.Error.Should().Contain("{not-json");
    }
}
