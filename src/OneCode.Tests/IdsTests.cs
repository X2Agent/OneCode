using OneCode.Core.Domain;

namespace OneCode.Tests;

/// <summary>
/// Tests for <see cref="AgentId"/> and <see cref="SessionId"/>
/// </summary>
public sealed class IdsTests
{
    [Fact]
    public void AgentId_NewId_GeneratesValidFormat()
    {
        var id = AgentId.NewId();

        id.Value.Should().NotBeNullOrEmpty();
        id.Value.Should().StartWith("a");
        id.Value.Length.Should().Be(17); // "a" + 16 hex chars
    }

    [Theory]
    [InlineData("a1a2b3c4d5e6f7a80")]
    [InlineData("a1234567890abcdef")]
    public void AgentId_TryParse_ValidFormat_ReturnsId(string validId)
    {
        var result = AgentId.TryParse(validId);

        result.Should().NotBeNull();
        result!.Value.Value.Should().Be(validId);
    }

    [Theory]
    [InlineData("b1a2b3c4d5e6f7a8")] // Wrong prefix
    [InlineData("a1a2b3c4d5e6f")] // Too short (15 hex)
    [InlineData("invalid")] // Invalid format
    [InlineData("")] // Empty
    public void AgentId_TryParse_InvalidFormat_ReturnsNull(string invalidId)
    {
        var result = AgentId.TryParse(invalidId);

        result.Should().BeNull();
    }
}
