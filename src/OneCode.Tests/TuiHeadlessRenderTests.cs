using OneCode.App.Tui;
using OneCode.Tests.TestSupport.Tui;

using Terminal.Gui.Input;

namespace OneCode.Tests;

/// <summary>
/// L3 驱动级：真实按键注入 → 真实布局绘制 → 从驱动屏幕缓冲读回渲染结果。
///
/// 这是 L2 结构上覆盖不到的一段：替身宿主没有绘制环节，断言只能停在「视图状态对不对」；
/// 这里断言的是「用户实际看到的那一屏」—— 字符真的被输入框吃掉、流式事件真的画进了会话区、
/// 全角文本真的按两列宽度排进了屏幕。
/// </summary>
public sealed class TuiHeadlessRenderTests
{
    [Fact]
    public async Task TypeAscii_EchoedToInputRow()
    {
        using var host = TuiHeadlessHost.Start();

        await host.TypeAsync("hello");
        var screen = await host.WaitForScreenAsync(
            text => text.Contains("hello", StringComparison.Ordinal),
            "键入的字符回显到输入行");

        screen.Should().Contain("hello", "真实驱动解码的字符必须一路走到输入框并画出来");
    }

    [Fact]
    public async Task SubmitQuery_StreamedCjkText_ReachesScreenIntact()
    {
        const string Reply = "流式中文回复标记";
        using var host = TuiHeadlessHost.Start(b => b.Stream(
            new TuiTextDelta(Reply),
            new TuiDone(3, 5)));

        await host.SubmitQueryAsync("read the config");

        var screen = await host.WaitForScreenAsync(
            text => text.Contains(Reply, StringComparison.Ordinal),
            "流式回复落进会话区");

        screen.Should().Contain(Reply, "查询事件必须经真实布局绘制后出现在屏幕缓冲里");
        // 全角字符占两列：若布局按「一字符一列」计算，这一行就会溢出并被驱动截掉尾部。
        var replyLine = screen.Split('\n').Single(line => line.Contains(Reply, StringComparison.Ordinal));
        TuiHeadlessHost.DisplayColumns(replyLine).Should().BeLessThanOrEqualTo(host.Cols);
    }

    [Fact]
    public async Task SubmitQuery_AfterReply_InputRowIsFocusableAgain()
    {
        using var host = TuiHeadlessHost.Start(b => b.Stream(new TuiTextDelta("回复一"), new TuiDone(1, 1)));

        await host.SubmitQueryAsync("first question");
        // 一轮结束后输入框必须解除忙碌：Ctrl+D 能被吃掉才说明焦点与忙碌状态都收尾了。
        await host.InjectKeyAsync(Key.D.WithCtrl);

        (await host.WaitForExitAsync()).Should().Be(0, "查询结束后输入框必须重新接受按键，否则用户被永久锁死");
    }
}
