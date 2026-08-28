using NSubstitute;
using OneCode.App.Tui;
using OneCode.Core.Keybindings;
using System.Drawing;
using Terminal.Gui.App;
using Terminal.Gui.Input;

namespace OneCode.Tests;

/// <summary>
/// 侧边栏键盘宽度调整（keyboard-first，替代分隔线鼠标拖拽的键盘路径）：
/// - app:sidebarWider/Narrower 默认绑定 Ctrl+Shift+←/→ 经 Resolver 解析（Global 上下文）；
/// - <see cref="SidebarViewBase.AdjustWidth"/> 与分隔线拖拽共用 clamp 规则
///   （MinWidth 下限、60% 屏宽上限），被 clamp 抵消时不触发重排回调。
/// </summary>
public sealed class SidebarKeyboardResizeTests
{
    // —— 默认绑定解析 ——

    [Fact]
    public void DefaultBindings_CtrlShiftArrows_MapToSidebarResizeActions()
    {
        var resolver = new KeybindingResolver();
        resolver.SetBindings([.. KeybindingDefaults.GetDefaultParsedBindings()]);
        var contexts = new KeybindingContextManager
        {
            FocusContext = KeybindingDefaults.ContextChat,
        }.ActiveContexts;

        TuiKeyAdapter.ResolveAction(Key.CursorRight.WithCtrl.WithShift, resolver, contexts)
            .Should().Be(KeybindingDefaults.ActionAppSidebarWider);
        TuiKeyAdapter.ResolveAction(Key.CursorLeft.WithCtrl.WithShift, resolver, contexts)
            .Should().Be(KeybindingDefaults.ActionAppSidebarNarrower);
    }

    // —— AdjustWidth clamp 规则 ——

    [Fact]
    public void AdjustWidth_Widening_ClampsAt60PercentOfScreen()
    {
        // 60% of 100 = 60：54 → 58 → 60（封顶），继续加宽不再变化
        var (view, _, widthChanges) = CreateView(screenWidth: 100);

        view.AdjustWidth(SidebarViewBase.KeyboardResizeStep).Should().BeTrue();
        view.CurrentWidth.Should().Be(54);
        view.AdjustWidth(SidebarViewBase.KeyboardResizeStep).Should().BeTrue();
        view.CurrentWidth.Should().Be(58);
        view.AdjustWidth(SidebarViewBase.KeyboardResizeStep).Should().BeTrue();
        view.CurrentWidth.Should().Be(60);
        view.AdjustWidth(SidebarViewBase.KeyboardResizeStep).Should().BeFalse(
            "已到 60% 屏宽上限，宽度不得超出");
        view.CurrentWidth.Should().Be(60);
        widthChanges.Should().HaveCount(3, "被 clamp 抵消的调整不触发重排回调");
    }

    [Fact]
    public void AdjustWidth_Narrowing_FloorsAtMinWidth()
    {
        var (view, _, _) = CreateView(screenWidth: 100);

        for (var i = 0; i < 5; i++)
            view.AdjustWidth(-SidebarViewBase.KeyboardResizeStep);

        view.CurrentWidth.Should().Be(SidebarViewBase.MinWidth);
        view.AdjustWidth(-SidebarViewBase.KeyboardResizeStep).Should().BeFalse(
            "已到 MinWidth 下限，宽度不得再收窄");
        view.CurrentWidth.Should().Be(SidebarViewBase.MinWidth);
    }

    [Fact]
    public void AdjustWidth_NarrowTerminal_ClampsToScreenWidthNotBeyond()
    {
        // 30 列终端放不下 MinWidth：首次收窄把超宽面板（默认 50）退化到屏幕宽，
        // 之后宽度钉在屏幕宽，两个方向都不再变化（与拖拽退化规则一致）。
        var (view, _, _) = CreateView(screenWidth: 30);

        view.AdjustWidth(-SidebarViewBase.KeyboardResizeStep).Should().BeTrue();
        view.CurrentWidth.Should().Be(30);
        view.AdjustWidth(-SidebarViewBase.KeyboardResizeStep).Should().BeFalse();
        view.AdjustWidth(SidebarViewBase.KeyboardResizeStep).Should().BeFalse();
        view.CurrentWidth.Should().Be(30);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(80)]
    [InlineData(40)]
    [InlineData(25)]
    public void AdjustWidth_ResultStaysWithinSharedClampRules(int screenWidth)
    {
        // 键盘调整结果必须落在拖拽 clamp 规则的区间内（两条路径规则一致）
        var (view, _, _) = CreateView(screenWidth);
        view.AdjustWidth(SidebarViewBase.KeyboardResizeStep);

        view.CurrentWidth.Should().BeGreaterThanOrEqualTo(SidebarViewBase.ComputeMinWidth(screenWidth));
        view.CurrentWidth.Should().BeLessThanOrEqualTo(SidebarViewBase.ComputeMaxWidth(screenWidth));
    }

    // —— 工具 ——

    private static (PlanSidebarView View, IApplication App, List<int> WidthChanges) CreateView(int screenWidth)
    {
        var app = Substitute.For<IApplication>();
        app.Screen.Returns(new Rectangle(0, 0, screenWidth, 40));

        var widthChanges = new List<int>();
        PlanSidebarView? view = null;
        view = new PlanSidebarView(
            app,
            widthChanged: () => widthChanges.Add(view!.CurrentWidth),
            dragEnded: () => { });
        return (view, app, widthChanges);
    }
}

