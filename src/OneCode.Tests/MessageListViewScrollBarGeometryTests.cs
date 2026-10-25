using OneCode.Tui;

namespace OneCode.Tests;

/// <summary>
/// 滚动条几何纯函数：绘制（DrawScrollIndicator）与拖动命中判断共用同一份计算。
/// 拖动反算（拇指顶部 → 滚动偏移）与正算（偏移 → 拇指位置）在端点精确互逆，
/// 中间点按像素量化误差（scrollRange / trackRange 向上取整）内互逆。
/// </summary>
public sealed class MessageListViewScrollBarGeometryTests
{
    private const int ViewportHeight = 40;
    private const int LineCount = 200;
    private const int ScrollRange = LineCount - ViewportHeight; // 160
    // 拇指尺寸 = viewport²/lines = 40²/200 = 8（四舍五入前的整除值）
    private const int ThumbSize = 8;
    private const int TrackRange = ViewportHeight - ThumbSize; // 32
    private const int QuantizationStep = (ScrollRange + TrackRange - 1) / TrackRange; // 5

    [Fact]
    public void ComputeScrollBarGeometry_ContentShorterThanViewport_NoThumb()
    {
        var (thumbSize, thumbPos) = MessageListView.ComputeScrollBarGeometry(10, viewportHeight: 20, scrollOffset: 0);

        thumbSize.Should().Be(0, "内容不超视口时无需滚动条");
        thumbPos.Should().Be(0);
    }

    [Fact]
    public void ComputeScrollBarGeometry_AtTop_ThumbStartsAtZero()
    {
        var (thumbSize, thumbPos) = MessageListView.ComputeScrollBarGeometry(LineCount, ViewportHeight, scrollOffset: 0);

        thumbSize.Should().Be(ThumbSize);
        thumbPos.Should().Be(0, "贴顶时拇指在轨道顶端");
    }

    [Fact]
    public void ComputeScrollBarGeometry_AtBottom_ThumbEndsAtTrackEnd()
    {
        var (thumbSize, thumbPos) = MessageListView.ComputeScrollBarGeometry(
            LineCount, ViewportHeight, scrollOffset: ScrollRange);

        // 贴底时拇指底缘 = 轨道底缘（viewport - thumbSize）
        (thumbPos + thumbSize).Should().Be(ViewportHeight);
    }

    [Fact]
    public void ComputeScrollBarGeometry_AtMidpoint_ThumbAtTrackMiddle()
    {
        var (_, thumbPos) = MessageListView.ComputeScrollBarGeometry(
            LineCount, ViewportHeight, scrollOffset: ScrollRange / 2);

        thumbPos.Should().Be(TrackRange / 2, "中点偏移的拇指应落在轨道中部");
    }

    [Theory]
    [InlineData(0, 0)]                                  // 端点：贴顶，精确还原
    [InlineData(ScrollRange, 0)]                        // 端点：贴底，精确还原
    [InlineData(ScrollRange / 3, QuantizationStep)]      // 中间点：像素量化误差内
    [InlineData(ScrollRange / 2, QuantizationStep)]      // 中间点：像素量化误差内
    public void ScrollOffsetFromThumbTop_EndpointsExactMidpointsWithinQuantization(
        int offset, int maxAbsError)
    {
        // 正算（偏移 → 拇指位置）再反算（拇指位置 → 偏移）：端点必须精确，
        // 中间点在像素量化误差内（拖动落点精度受轨道分辨率限制，这是可接受的）。
        var (_, thumbPos) = MessageListView.ComputeScrollBarGeometry(LineCount, ViewportHeight, offset);
        var restored = MessageListView.ScrollOffsetFromThumbTop(thumbPos, ThumbSize, ViewportHeight, LineCount);

        Math.Abs(restored - offset).Should().BeLessThanOrEqualTo(maxAbsError,
            $"offset {offset} 经拇指几何往返后应还原（量化步长 {QuantizationStep}）");
    }

    [Fact]
    public void ScrollOffsetFromThumbTop_ClampsToValidRange()
    {
        MessageListView.ScrollOffsetFromThumbTop(-10, ThumbSize, ViewportHeight, LineCount)
            .Should().Be(0, "拖出轨道顶端钳制到 0");
        MessageListView.ScrollOffsetFromThumbTop(ViewportHeight + 10, ThumbSize, ViewportHeight, LineCount)
            .Should().Be(ScrollRange, "拖出轨道底端钳制到最大偏移");
    }
}
