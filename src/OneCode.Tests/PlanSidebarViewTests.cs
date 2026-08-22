using OneCode.App.Tui;

namespace OneCode.Tests;

/// <summary>
/// Drag-width clamp rules for <see cref="PlanSidebarView.ComputeDraggedWidth"/>:
/// lower bound MinWidth, upper bound min(60% of screen, screen - chat column
/// minimum), and no width may ever exceed the screen (negative AnchorEnd X)
/// or violate Math.Clamp's max >= min contract on tiny terminals.
/// </summary>
public sealed class PlanSidebarViewTests
{
    [Theory]
    [InlineData(100, 10, 60)]   // wide terminal: capped at 60% of screen
    [InlineData(100, 68, 32)]   // dragged past the lower bound: clamps to MinWidth
    [InlineData(120, 30, 72)]   // 60% of 120
    [InlineData(80, 0, 48)]     // 60% of 80
    public void ComputeDraggedWidth_WideScreen_ClampsTo60Percent(int screenWidth, int screenX, int expected)
    {
        PlanSidebarView.ComputeDraggedWidth(screenX, screenWidth).Should().Be(expected);
    }

    [Theory]
    [InlineData(50, 0)]         // 60% (30) and chat-min (30) both below MinWidth → MinWidth floor
    [InlineData(45, 10)]        // screen - 20 = 25 < MinWidth → MinWidth floor
    public void ComputeDraggedWidth_NarrowScreen_FloorsToMinWidth(int screenWidth, int screenX)
    {
        // Narrow terminals cannot honour both panel MinWidth and the chat column
        // minimum; the panel keeps its minimum usable width.
        PlanSidebarView.ComputeDraggedWidth(screenX, screenWidth).Should().Be(PlanSidebarView.MinWidth);
    }

    [Fact]
    public void ComputeDraggedWidth_TerminalNarrowerThanMinWidth_NeverExceedsScreenWidth()
    {
        // A 30-column terminal cannot fit MinWidth at all — the width must
        // degenerate to the screen width (never larger, never an exception).
        PlanSidebarView.ComputeDraggedWidth(0, 30).Should().Be(30);
        PlanSidebarView.ComputeDraggedWidth(29, 30).Should().Be(30);
    }
}
