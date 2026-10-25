using NSubstitute;
using OneCode.App.Services.PlanMode;
using OneCode.Core.Keybindings;
using System.Drawing;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using OneCode.Tui;

namespace OneCode.Tests;

/// <summary>
/// 侧边栏键盘宽度调整（keyboard-first，替代分隔线鼠标拖拽的键盘路径）：
/// - app:sidebarWider/Narrower 默认绑定 Ctrl+Shift+←/→ 经 Resolver 解析（Global 上下文）；
/// - <see cref="SidebarViewBase.AdjustWidth"/> 与分隔线拖拽共用 clamp 规则
///   （MinWidth 下限、40% 屏宽与 100 列封顶、主区保底 65 列的上限），被 clamp 抵消时不触发重排回调；
/// - 宽度默认按屏宽 40% 比例自适应，终端 resize 时跟随（手动调过后只 clamp）。
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
    public void AdjustWidth_Widening_ClampsAtProportionalMax()
    {
        // 140 列终端：比例默认 56 已是上限（40% 屏宽），继续加宽被 clamp 抵消
        var (view, _, widthChanges) = CreateView(screenWidth: 140);

        view.CurrentWidth.Should().Be(56, "默认宽度按屏宽 40% 自适应（140 × 40% = 56）");
        view.AdjustWidth(SidebarViewBase.KeyboardResizeStep).Should().BeFalse(
            "已到 40% 屏宽上限 (56 列)，宽度不得超出");
        widthChanges.Should().BeEmpty("被 clamp 抵消的调整不触发重排回调");
    }

    [Fact]
    public void AdjustWidth_Widening_OnWideScreen_ClampsAtProportionalMaxBeforeCap()
    {
        // 200 列终端：比例默认 80 列（40%），未触及 100 列封顶；主区保底 65 列不破
        var (view, _, _) = CreateView(screenWidth: 200);

        view.CurrentWidth.Should().Be(80);
        view.AdjustWidth(SidebarViewBase.KeyboardResizeStep).Should().BeFalse(
            "已到 40% 屏宽上限 (80 列)，宽度不得超出");
    }

    [Fact]
    public void AdjustWidth_Widening_OnUltrawideScreen_ClampsAtHundredColumnCap()
    {
        // 300 列终端：40% 比例（120）超过 100 列封顶，默认宽度落在封顶值
        var (view, _, _) = CreateView(screenWidth: 300);

        view.CurrentWidth.Should().Be(100, "超宽终端按 MaxWidthCap=100 封顶，避免挤占主区");
        view.AdjustWidth(SidebarViewBase.KeyboardResizeStep).Should().BeFalse(
            "已到 100 列封顶，宽度不得超出");
    }

    [Fact]
    public void ComputeMaxWidth_ScalesProportionallyWithScreenWidth()
    {
        // 40% 比例上限、100 列封顶、主区保底 65 列、极窄终端退化为屏幕宽
        SidebarViewBase.ComputeMaxWidth(25).Should().Be(25, "极窄终端退化为屏幕宽");
        SidebarViewBase.ComputeMaxWidth(80).Should().Be(28, "80-65=15 低于 MinWidth 时抬升到下限");
        SidebarViewBase.ComputeMaxWidth(100).Should().Be(35, "100-65=35 低于 40% 比例（40），主区保底优先");
        SidebarViewBase.ComputeMaxWidth(120).Should().Be(48);
        SidebarViewBase.ComputeMaxWidth(140).Should().Be(56);
        SidebarViewBase.ComputeMaxWidth(160).Should().Be(64);
        SidebarViewBase.ComputeMaxWidth(200).Should().Be(80);
        SidebarViewBase.ComputeMaxWidth(250).Should().Be(100, "40% 比例（100）恰好等于封顶值");
        SidebarViewBase.ComputeMaxWidth(300).Should().Be(100, "超宽屏封顶 100 列（40% 比例过大）");
    }

    [Fact]
    public void ComputeDefaultWidth_FollowsFortyPercentOfScreen()
    {
        SidebarViewBase.ComputeDefaultWidth(140).Should().Be(56);
        SidebarViewBase.ComputeDefaultWidth(200).Should().Be(80);
        SidebarViewBase.ComputeDefaultWidth(300).Should().Be(100, "40% 比例被 100 列封顶");
        SidebarViewBase.ComputeDefaultWidth(100).Should().Be(35, "受主区保底 65 列约束");
        SidebarViewBase.ComputeDefaultWidth(80).Should().Be(28, "低于比例下限时抬升到 MinWidth");
        SidebarViewBase.ComputeDefaultWidth(25).Should().Be(25, "极窄终端退化为屏幕宽");
    }

    [Fact]
    public void OnTerminalResized_UntouchedWidth_FollowsNewProportionalDefault()
    {
        // 用户未手动调整：从 140 列（默认 56）放大到 200 列 → 宽度跟随到 80；
        // 缩小到 100 列 → 收缩到 35。侧边栏应自适应窗口尺寸。
        var (view, _, _) = CreateView(screenWidth: 140);
        view.CurrentWidth.Should().Be(56);

        view.OnTerminalResized(200).Should().BeTrue();
        view.CurrentWidth.Should().Be(80);

        view.OnTerminalResized(100).Should().BeTrue();
        view.CurrentWidth.Should().Be(35);
    }

    [Fact]
    public void OnTerminalResized_UserAdjustedWidth_KeepsChoiceButClampsToNewMax()
    {
        // 用户手动调过宽度：resize 尊重其选择；新窗口放不下时才按新上限 clamp
        var (view, _, _) = CreateView(screenWidth: 200);
        view.AdjustWidth(-SidebarViewBase.KeyboardResizeStep); // 80 → 76，标记手动调整
        view.CurrentWidth.Should().Be(76);

        view.OnTerminalResized(140).Should().BeTrue("76 列超出 140 列终端的 56 列上限，必须 clamp");
        view.CurrentWidth.Should().Be(56);
        view.OnTerminalResized(140).Should().BeFalse("宽度已在新边界内，重复 resize 不再变化");
    }

    [Fact]
    public void AdjustWidth_Narrowing_FloorsAtMinWidth()
    {
        var (view, _, _) = CreateView(screenWidth: 100); // 默认 35（受主区保底约束）

        view.AdjustWidth(-SidebarViewBase.KeyboardResizeStep).Should().BeTrue(); // 35 → 31
        view.AdjustWidth(-SidebarViewBase.KeyboardResizeStep).Should().BeTrue(); // 31 → 28（27 被 clamp）
        view.CurrentWidth.Should().Be(SidebarViewBase.MinWidth);
        view.AdjustWidth(-SidebarViewBase.KeyboardResizeStep).Should().BeFalse(
            "已到 MinWidth 下限，宽度不得再收窄");
        view.CurrentWidth.Should().Be(SidebarViewBase.MinWidth);
    }

    [Fact]
    public void AdjustWidth_NarrowTerminal_ClampsToScreenWidthNotBeyond()
    {
        // 25 列终端放不下 MinWidth(28)：退化到屏幕宽，两个方向都不再变化
        var (view, _, _) = CreateView(screenWidth: 25);

        view.CurrentWidth.Should().Be(25);
        view.AdjustWidth(-SidebarViewBase.KeyboardResizeStep).Should().BeFalse();
        view.AdjustWidth(SidebarViewBase.KeyboardResizeStep).Should().BeFalse();
        view.CurrentWidth.Should().Be(25);
    }

    [Theory]
    [InlineData(93, 28, true)]
    [InlineData(94, 28, false)]
    [InlineData(100, 30, false)]
    [InlineData(100, 35, true)]
    public void ShouldHideSidebarForWidth_PreservesMinimumChatWidth(
        int screenWidth, int sidebarWidth, bool expected)
    {
        ReplShell.ShouldHideSidebarForWidth(screenWidth, sidebarWidth).Should().Be(expected);
    }

    [Theory]
    [InlineData(140)]
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

    [Theory]
    [InlineData(PlanCardPhase.Completed, " · 已完成")]
    [InlineData(PlanCardPhase.Executing, " · 正在执行")]
    [InlineData(PlanCardPhase.PendingApproval, " · 等待审批")]
    public void FormatPlanHeaderTitle_LongTitle_PreservesPhaseStatusSuffixWithinSidebarWidth(
        PlanCardPhase phase, string expectedSuffix)
    {
        var plan = new PlanCardState(
            "孙宇晨近半月新闻 · 币安风格 HTML 时间线",
            [new PlanStep("生成单文件 HTML", Status: PlanStepStatus.Done)],
            phase);

        var formatted = ReplShell.FormatPlanHeaderTitle(plan, sidebarWidth: 42);

        formatted.Should().EndWith(expectedSuffix,
            "the phase status suffix must be preserved even when the plan title exceeds sidebar width");
        TextWidthHelper.GetDisplayWidth($" \U0001f4cb {formatted}")
            .Should().BeLessThan(42, "SidebarViewBase must not clip the trailing status suffix on draw");
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
