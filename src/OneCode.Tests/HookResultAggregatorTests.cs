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
        aggregated.BlockingErrors.Should().BeNull();
        aggregated.AdditionalContexts.Should().BeNull();
    }

    [Fact]
    public void Aggregate_SingleResult_ReturnsSameValues()
    {
        var result = new HookResult
        {
            Message = "Message 1",
            AdditionalContext = "ctx-1",
        };

        var aggregated = HookResultAggregator.Aggregate(new[] { result });

        aggregated.Message.Should().Be("Message 1");
        aggregated.AdditionalContexts.Should().ContainSingle().Which.Should().Be("ctx-1");
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
    public void Aggregate_SystemMessage_UsedWhenMessageAbsent()
    {
        var result = new HookResult { SystemMessage = "system-only" };

        var aggregated = HookResultAggregator.Aggregate(new[] { result });

        aggregated.Message.Should().Be("system-only");
    }

    [Fact]
    public void Aggregate_MultipleResults_MergesAdditionalContextsInExecutionOrder()
    {
        var result1 = new HookResult { AdditionalContext = "ctx-1" };
        var result2 = new HookResult { AdditionalContext = "ctx-2" };

        var aggregated = HookResultAggregator.Aggregate(new[] { result1, result2 });

        aggregated.AdditionalContexts.Should().ContainInOrder("ctx-1", "ctx-2");
    }

    [Fact]
    public void Aggregate_BlockingErrors_AreCollectedInExecutionOrder()
    {
        var result1 = new HookResult { BlockingError = new HookBlockingError("deny-1", "cmd-1") };
        var result2 = new HookResult { BlockingError = new HookBlockingError("deny-2", "cmd-2") };

        var aggregated = HookResultAggregator.Aggregate(new[] { result1, result2 });

        aggregated.BlockingErrors.Should().HaveCount(2);
        aggregated.BlockingErrors!.Select(b => b.Error).Should().Equal("deny-1", "deny-2");
    }

    [Fact]
    public void Aggregate_SingleBlockingError_YieldsDeny()
    {
        var result = new HookResult { BlockingError = new HookBlockingError("forbidden", "cmd") };

        var aggregated = HookResultAggregator.Aggregate(new[] { result });

        aggregated.BlockingErrors.Should().ContainSingle(b => b.Error == "forbidden");
    }

    [Fact]
    public void Aggregate_NullResults_SkipsNulls()
    {
        var result1 = new HookResult { Message = "Msg1" };
        var result2 = new HookResult { Message = "Msg2" };

        var aggregated = HookResultAggregator.Aggregate(new HookResult?[] { result1, null, result2 });

        aggregated.Message.Should().Be("Msg2"); // last-write-wins
    }
}
