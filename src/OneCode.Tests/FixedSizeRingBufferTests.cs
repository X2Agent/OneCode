using OneCode.Core.Collections;

namespace OneCode.Tests;

/// <summary>
/// Tests for <see cref="FixedSizeRingBuffer{T}"/>
/// </summary>
public sealed class FixedSizeRingBufferTests
{
    [Fact]
    public void Constructor_ZeroCapacity_ThrowsArgumentOutOfRangeException()
    {
        var act = () => new FixedSizeRingBuffer<int>(0);
        act.Should().Throw<ArgumentOutOfRangeException>()
            .And.ParamName.Should().Be("capacity");
    }

    [Fact]
    public void Constructor_NegativeCapacity_ThrowsArgumentOutOfRangeException()
    {
        var act = () => new FixedSizeRingBuffer<int>(-5);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Add_SingleItem_CountIsOne()
    {
        var buffer = new FixedSizeRingBuffer<int>(5);
        buffer.Add(42);

        buffer.Count.Should().Be(1);
        buffer.IsFull.Should().BeFalse();
    }

    [Fact]
    public void Add_UntilFull_CountEqualsCapacity()
    {
        var buffer = new FixedSizeRingBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        buffer.Count.Should().Be(3);
        buffer.IsFull.Should().BeTrue();
    }

    [Fact]
    public void Add_OverCapacity_OverwritesOldest()
    {
        var buffer = new FixedSizeRingBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);
        buffer.Add(4); // Overwrites 1

        buffer.Count.Should().Be(3);
        var items = buffer.AsEnumerable().ToList();
        items.Should().Equal(2, 3, 4);
    }

    [Fact]
    public void Add_MultipleOverwrites_MaintainsOrder()
    {
        var buffer = new FixedSizeRingBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);
        buffer.Add(4); // Overwrites 1
        buffer.Add(5); // Overwrites 2
        buffer.Add(6); // Overwrites 3

        buffer.Count.Should().Be(3);
        var items = buffer.AsEnumerable().ToList();
        items.Should().Equal(4, 5, 6);
    }

    [Fact]
    public void AsEnumerable_PartiallyFull_ReturnsInOrder()
    {
        var buffer = new FixedSizeRingBuffer<int>(5);
        buffer.Add(10);
        buffer.Add(20);

        buffer.AsEnumerable().Should().Equal(10, 20);
    }

    [Fact]
    public void AsEnumerable_WrappedAround_ReturnsChronologicalOrder()
    {
        var buffer = new FixedSizeRingBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);
        buffer.Add(4); // Wraps around

        var items = buffer.AsEnumerable().ToList();
        items.Should().Equal(2, 3, 4);
    }

    [Fact]
    public void LastOrDefault_WithItems_ReturnsMostRecent()
    {
        var buffer = new FixedSizeRingBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        buffer.LastOrDefault().Should().Be(3);
    }

    [Fact]
    public void LastOrDefault_AfterOverwrite_ReturnsLatest()
    {
        var buffer = new FixedSizeRingBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);
        buffer.Add(4);

        buffer.LastOrDefault().Should().Be(4);
    }

    [Fact]
    public void Clear_ResetsCountAndContent()
    {
        var buffer = new FixedSizeRingBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);
        buffer.Clear();

        buffer.Count.Should().Be(0);
        buffer.IsFull.Should().BeFalse();
        buffer.AsEnumerable().Should().BeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(100)]
    public void Add_StringItems_MaintainsCorrectOrder(int capacity)
    {
        var buffer = new FixedSizeRingBuffer<string>(capacity);
        for (var i = 0; i < capacity + 2; i++)
        {
            buffer.Add($"item-{i}");
        }

        var items = buffer.AsEnumerable().ToList();
        items.Should().HaveCount(capacity);
        items[0].Should().Be("item-2");
        items[^1].Should().Be($"item-{capacity + 1}");
    }
}
