using OneCode.App.Tui;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace OneCode.Tests;

/// <summary>
/// McpConfigOverlay 标题与双栏布局的无头（headless）回归测试。
/// 背景缺陷：宿主 OverlayHost 会把对话框宽度钳制到终端宽度的 70%（下限 40 列），
/// 旧布局右栏用 Dim.Fill(45) 定宽，窄终端下"方法"面板宽度算出 ≤ 0 而整栏消失，
/// 只能对服务器整体启停、看不到方法级勾选。
/// </summary>
public sealed class McpConfigOverlayTests
{
    [Fact]
    public void Title_ContainsOnlyPageName()
    {
        CreateOverlay().Title.Should().Be("MCP 工具白名单");
    }

    /// <summary>
    /// 按 OverlayHost 真实钳制路径把 overlay 定位到模拟终端尺寸并完成布局。
    /// （Dialog 模式 overlay 的宽度钳制在 Position，Layout(Size) 不会驱动它。）
    /// </summary>
    private static McpConfigOverlay LayoutAt(int terminalWidth, int terminalHeight = 40)
    {
        var host = new OverlayHost(() => { });
        var overlay = CreateOverlay();
        host.Push(overlay);
        host.Position(overlay, terminalWidth, terminalHeight);
        overlay.Layout();
        return overlay;
    }

    [Theory]
    [InlineData(88)] // 常规终端宽（对话框被钳到 70% = 61 列）
    [InlineData(63)] // 90 列终端 × 70%
    [InlineData(48)] // 68 列终端 × 70%
    [InlineData(40)] // OverlayHost 允许的最小对话框宽度
    public void Layout_NarrowTerminal_KeepsMethodPaneBesideServerPane(int terminalWidth)
    {
        var overlay = LayoutAt(terminalWidth);

        var serverFrame = overlay.ServerList.SuperView!;
        var toolFrame = overlay.ToolList.SuperView!;

        // 回归形态：旧布局方法窗格在窄终端下整栏消失。
        toolFrame.Frame.Width.Should().BeGreaterThanOrEqualTo(TuiSpacing.McpToolPaneMinWidth);
        serverFrame.Frame.Width.Should().BeGreaterThanOrEqualTo(TuiSpacing.McpServerPaneMinWidth);
        serverFrame.Frame.Right.Should().BeLessThanOrEqualTo(toolFrame.Frame.X);
        toolFrame.Frame.Right.Should().BeLessThanOrEqualTo(overlay.Frame.Width);
    }

    [Fact]
    public void Layout_WideTerminal_KeepsRegularServerPaneWidth()
    {
        // 终端 132 列 → 对话框 84（preferred），服务器窗格才保得住常规 36。
        var overlay = LayoutAt(132);

        overlay.ServerList.SuperView!.Frame.Width.Should().Be(TuiSpacing.McpServerPaneWidth);
    }

    [Fact]
    public async Task TrySave_TouchedWhitelist_PersistsCheckedMethodsOnly()
    {
        var host = new OverlayHost(() => { });
        var overlay = CreateOverlay();
        var saved = overlay.ShowAsync(host.Push, () => host.Pop(), TestContext.Current.CancellationToken);

        overlay.ToolList.SelectedItem = 1; // get-docs
        overlay.ToggleTool();              // 取消勾选
        overlay.TrySave();

        var result = await saved;
        var change = result!.Servers.Should().ContainSingle().Subject;
        change.Name.Should().Be("context7");
        change.EnabledTools.Should().BeEquivalentTo("resolve", "search");
        change.Disabled.Should().BeNull();
    }

    [Fact]
    public async Task TrySave_WithoutTouchingWhitelist_ProducesNoServerChanges()
    {
        // 未动方法清单就保存：不得把"未配置（全部暴露）"折叠成空名单写回配置。
        var host = new OverlayHost(() => { });
        var overlay = CreateOverlay();
        var saved = overlay.ShowAsync(host.Push, () => host.Pop(), TestContext.Current.CancellationToken);

        overlay.TrySave();

        (await saved)!.Servers.Should().BeEmpty();
    }

    [Fact]
    public async Task TrySave_ServerDisabledViaCheckbox_ReportsDisabledChange()
    {
        var host = new OverlayHost(() => { });
        var overlay = CreateOverlay();
        var saved = overlay.ShowAsync(host.Push, () => host.Pop(), TestContext.Current.CancellationToken);

        // 与真实路径一致：CheckBox 切到未勾选 → ValueChanged → ToggleServerEnabled。
        overlay.ServerEnabledCheck.Value = CheckState.UnChecked;
        overlay.ToggleServerEnabled();
        overlay.TrySave();

        var result = await saved;
        var change = result!.Servers.Should().ContainSingle().Subject;
        change.Disabled.Should().BeTrue();
        change.EnabledTools.Should().BeNull();
    }

    [Fact]
    public void Keyboard_ArrowKeys_SwitchPanesDirectionally()
    {
        // ←→ 按布局方向在两个面板间切换：服务器列表按 → 进方法列表，
        // 方法列表按 ← 回服务器列表（与顶部提示一致）。
        // 从 overlay 根部分发按键（模拟真实终端的冒泡链路），并断言原面板
        // 真正失去焦点——历史缺陷：只断言目标面板 HasFocus 会在"焦点未切换
        // 但目标面板残留 HasFocus"的异常下恒真。
        var overlay = LayoutAt(88);

        overlay.ServerList.HasFocus.Should().BeTrue();

        overlay.NewKeyDownEvent(Key.CursorRight);
        overlay.ToolList.HasFocus.Should().BeTrue();
        overlay.ServerList.HasFocus.Should().BeFalse();

        overlay.NewKeyDownEvent(Key.CursorLeft);
        overlay.ServerList.HasFocus.Should().BeTrue();
        overlay.ToolList.HasFocus.Should().BeFalse();
    }

    [Fact]
    public void Keyboard_Tab_FollowsFrameworkTabStopChain()
    {
        // Tab/Shift+Tab 不再在两个列表间往返（避免把焦点困住），
        // 而是沿用框架 TabStop 链遍历全部控件：
        // 服务器列表 → 方法列表 → 启用复选框 → 操作栏（保存/取消）。
        // 视图未消费的按键按 View.NewKeyDownEvent → 视图命令 → 应用命令的
        // 顺序触发 NextTabStop/PreviousTabStop，此处经 overlay 根部派发。
        var overlay = LayoutAt(88);

        overlay.ServerList.HasFocus.Should().BeTrue();

        overlay.NewKeyDownEvent(Key.Tab);
        overlay.ToolList.HasFocus.Should().BeTrue();
        overlay.ServerList.HasFocus.Should().BeFalse();

        overlay.NewKeyDownEvent(Key.Tab);
        overlay.ServerEnabledCheck.HasFocus.Should().BeTrue();
        overlay.ToolList.HasFocus.Should().BeFalse();

        overlay.NewKeyDownEvent(Key.Tab);
        overlay.Actions.HasFocus.Should().BeTrue();
        overlay.ServerEnabledCheck.HasFocus.Should().BeFalse();

        // Shift+Tab 沿链回退：操作栏 → 启用复选框。
        overlay.NewKeyDownEvent(Key.Tab.WithShift);
        overlay.ServerEnabledCheck.HasFocus.Should().BeTrue();
        overlay.Actions.HasFocus.Should().BeFalse();
    }

    [Fact]
    public void Keyboard_Space_TogglesViaListAndCheckbox()
    {
        // 空格语义与焦点所在面板一致：列表上勾选/取消方法，复选框上启停服务器。
        var overlay = LayoutAt(88);
        overlay.ServerList.SelectedItem = 0;

        // 焦点在方法列表：空格取消勾选 → 白名单被触碰。
        overlay.NewKeyDownEvent(Key.CursorRight);
        overlay.ToolList.HasFocus.Should().BeTrue();
        overlay.ToolList.SelectedItem = 1;
        overlay.NewKeyDownEvent(Key.Space);

        // 焦点在服务器复选框：空格切换启停（Checked → UnChecked）。
        overlay.NewKeyDownEvent(Key.Tab);
        overlay.ServerEnabledCheck.HasFocus.Should().BeTrue();
        overlay.NewKeyDownEvent(Key.Space);

        overlay.ServerEnabledCheck.Value.Should().Be(CheckState.UnChecked);
    }

    [Fact]
    public void Hints_MergeTopHint_AndRemoveMethodPaneTitleHint()
    {
        // 服务器/方法的空格行为一致（切换选中状态）：顶部合并为单条提示，
        // 方法窗格标题不再重复"空格 勾选/取消"。
        var overlay = LayoutAt(88);

        overlay.HintText.Should().Be("←→ 切换面板 · Tab 遍历控件 · 空格 勾选/取消");
        overlay.ToolList.SuperView!.Title.Should().Be(" 方法 ");
    }

    private static McpConfigOverlay CreateOverlay() => new(
    [
        new McpConfigServerEntry(
            Name: "context7",
            Disabled: false,
            Connected: true,
            EnabledTools: null,
            LiveTools: ["resolve", "get-docs", "search"]),
    ]);
}