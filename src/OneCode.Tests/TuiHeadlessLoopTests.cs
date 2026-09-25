using OneCode.Tests.TestSupport.Tui;

using Terminal.Gui.Input;

namespace OneCode.Tests;

/// <summary>
/// L3 驱动级：真实 <see cref="Terminal.Gui.App.IApplication"/> 主循环 + 真实 <c>ansi</c> 驱动的
/// 启动与退出。
///
/// L2（替身宿主）能证明「按键被分发到了正确的处理链」，但证明不了「这份分发链装在一个真能
/// 跑起来、真能画出画面、真能被真实按键编码触发的宿主里」。本类断言的是后者：
/// 主循环起得来、屏幕画得出、Ctrl+D / <c>/quit</c> 能让 <c>TuiHost.RunLoop</c> 干净返回 0。
/// </summary>
public sealed class TuiHeadlessLoopTests
{
    private const string ModelName = "test-model";

    [Fact]
    public async Task Start_RealAnsiDriver_ScreenIsFullyRendered()
    {
        using var host = TuiHeadlessHost.Start();
        await host.SettleAsync();

        var lines = await host.ScreenLinesAsync();

        lines.Should().HaveCount(host.Rows, "驱动屏幕缓冲必须覆盖整屏，行数少一行都说明布局没铺满");
        lines.Should().Contain(
            line => line.Contains(ModelName, StringComparison.Ordinal),
            "状态栏（模型名 · 沙箱 · 模式）是启动后必然可见的界面骨架");
        lines.Should().Contain(line => line.Contains("BUILD", StringComparison.Ordinal), "默认模式为 BUILD");
        // 全角字符按 2 列计的布局：这条提示行约 66 显示列，宽度算错就会被驱动截断成半行。
        lines.Should().Contain(
            line => line.Contains("/ 斜杠命令 · @ 提及文件 · Tab 循环切模式", StringComparison.Ordinal),
            "含中日韩全角的提示行必须完整落屏，不能被截断");
    }

    [Fact]
    public async Task CtrlD_InjectedThroughRealDriver_ExitsWithZeroCode()
    {
        using var host = TuiHeadlessHost.Start();

        await host.InjectKeyAsync(Key.D.WithCtrl);
        var exitCode = await host.WaitForExitAsync();

        exitCode.Should().Be(0, "Ctrl+D 是主动退出，属正常终止");
    }

    [Fact]
    public async Task SlashQuit_TypedThroughRealDriver_ExitsWithZeroCode()
    {
        using var host = TuiHeadlessHost.Start();

        await host.TypeAsync("/quit");
        await host.InjectKeyAsync(Key.Enter);
        var exitCode = await host.WaitForExitAsync();

        exitCode.Should().Be(0, "/quit 与 Ctrl+D 必须走同一条正常退出路径");
    }
}
