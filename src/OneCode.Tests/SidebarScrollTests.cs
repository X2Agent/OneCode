using NSubstitute;
using Terminal.Gui.App;
using OneCode.Tui;

namespace OneCode.Tests;

/// <summary>
/// 侧栏滚动行为：翻页转发到内嵌滚动体系；内容更新（执行进度推进）保留阅读
/// 偏移——用户正翻阅计划时面板刷新不再跳回底部。
/// </summary>
public sealed class SidebarScrollTests
{
    private static PlanSidebarView CreateSidebar()
    {
        var app = Substitute.For<IApplication>();
        app.Invoke(Arg.Do<Action>(action => action()));
        app.AddTimeout(Arg.Any<TimeSpan>(), Arg.Any<Func<bool>>()).Returns(true);
        return new PlanSidebarView(app, () => { }, () => { });
    }

    private static IReadOnlyList<FormattedLine> BuildLines(int count, string prefix = "line")
        => Enumerable.Range(0, count)
            .Select(i => FormattedLine.Plain($"{prefix}-{i:000}", TuiPalette.FgSecondary))
            .ToList();

    [Fact]
    public void PageUp_PageDown_DelegateToContentScroll()
    {
        var sidebar = CreateSidebar();
        sidebar.Update(BuildLines(80), "计划");

        // 未挂载到宿主时视口高度未解析（≥1 行步进），断言位移与状态而非具体页大小。
        sidebar.PageDown();
        var afterPageDown = sidebar.ScrollOffset;
        afterPageDown.Should().BeGreaterThan(0, "PageDown 向下翻页产生位移");
        sidebar.IsAutoScroll.Should().BeFalse("下翻未到底 = 阅读态，脱离贴底跟随");

        sidebar.PageUp();
        sidebar.ScrollOffset.Should().BeLessThan(afterPageDown, "PageUp 向上翻页");
    }

    [Fact]
    public void Update_WhileUserIsReading_PreservesScrollOffset()
    {
        var sidebar = CreateSidebar();
        sidebar.Update(BuildLines(80), "计划");

        sidebar.PageDown();
        var readingOffset = sidebar.ScrollOffset;
        readingOffset.Should().BeGreaterThan(0);

        // 执行进度推进 → 侧栏内容整体替换；阅读位置必须保留（按新行数钳制）。
        sidebar.Update(BuildLines(90), "计划 · 执行中");

        sidebar.ScrollOffset.Should().Be(readingOffset, "内容更新不清掉用户的阅读位置");
        sidebar.IsAutoScroll.Should().BeFalse();
    }

    [Fact]
    public void Update_WhileFollowingBottom_StaysAtBottom()
    {
        var sidebar = CreateSidebar();
        sidebar.Update(BuildLines(40), "计划");
        sidebar.IsAutoScroll.Should().BeTrue("初始贴底跟随");

        sidebar.Update(BuildLines(60), "计划 · 更新");

        sidebar.IsAutoScroll.Should().BeTrue("未主动翻页时保持贴底跟随");
    }
}
