using OneCode.Core.Hooks;

namespace OneCode.Tests;

/// <summary>
/// Tests for <see cref="HookResultAggregator"/>
/// </summary>
public sealed class HookResultAggregatorTests
{
    [Fact]
    public void Aggregate_EmptyResults_ReturnsEmptyAggregatedResult()
    {
        var results = new List<HookResult>();

        var aggregated = HookResultAggregator.Aggregate(results);

        aggregated.Should().NotBeNull();
        aggregated.Message.Should().BeNull();
        aggregated.UpdatedInput.Should().BeNull();
    }

    [Fact]
    public void Aggregate_SingleResult_ReturnsSameValues()
    {
        var result = new HookResult
        {
            Message = "Message 1",
            UpdatedInput = new Dictionary<string, object> { ["key"] = "value" },
        };

        var aggregated = HookResultAggregator.Aggregate(new[] { result });

        aggregated.Message.Should().Be("Message 1");
        aggregated.UpdatedInput.Should().ContainKey("key");
    }

    [Fact]
    public void Aggregate_MultipleResults_MergesMessages()
    {
        var result1 = new HookResult { Message = "Msg1" };
        var result2 = new HookResult { Message = "Msg2" };
        var result3 = new HookResult { Message = "Msg3" };

        var aggregated = HookResultAggregator.Aggregate(new[] { result1, result2, result3 });

        aggregated.Message.Should().Be("Msg3"); // last-write-wins
    }

    [Fact]
    public void Aggregate_MultipleResults_MergesUpdatedInput()
    {
        var result1 = new HookResult
        {
            UpdatedInput = new Dictionary<string, object> { ["key1"] = "value1" }
        };
        var result2 = new HookResult
        {
            UpdatedInput = new Dictionary<string, object> { ["key2"] = "value2" }
        };

        var aggregated = HookResultAggregator.Aggregate(new[] { result1, result2 });

        aggregated.UpdatedInput.Should().ContainKey("key2");
        aggregated.UpdatedInput!["key2"].Should().Be("value2");
    }

    [Fact]
    public void Aggregate_MultipleResults_LaterInputOverwritesEarlier()
    {
        var result1 = new HookResult
        {
            UpdatedInput = new Dictionary<string, object> { ["key"] = "value1" }
        };
        var result2 = new HookResult
        {
            UpdatedInput = new Dictionary<string, object> { ["key"] = "value2" }
        };

        var aggregated = HookResultAggregator.Aggregate(new[] { result1, result2 });

        aggregated.UpdatedInput!["key"].Should().Be("value2");
    }

    [Fact]
    public void Aggregate_NullResults_SkipsNulls()
    {
        var result1 = new HookResult { Message = "Msg1" };
        var result2 = new HookResult { Message = "Msg2" };

        var aggregated = HookResultAggregator.Aggregate(new HookResult?[] { result1, null, result2 });

        aggregated.Message.Should().Be("Msg2"); // last-write-wins
    }

    [Fact]
    public void Aggregate_ResultWithNullFields_SkipsNullFields()
    {
        var result1 = new HookResult
        {
            Message = "Msg1",
            UpdatedInput = null,
        };
        var result2 = new HookResult
        {
            Message = null,
            UpdatedInput = new Dictionary<string, object> { ["key"] = "value" },
        };

        var aggregated = HookResultAggregator.Aggregate(new[] { result1, result2 });

        // Message: last non-null wins (Msg1 kept because result2.Message is null)
        aggregated.Message.Should().Be("Msg1");
        aggregated.UpdatedInput.Should().ContainKey("key");
    }
}
