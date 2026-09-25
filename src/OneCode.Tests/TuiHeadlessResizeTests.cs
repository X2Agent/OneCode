using OneCode.App.Tui;
using OneCode.Tests.TestSupport.Tui;

namespace OneCode.Tests;

/// <summary>
/// L3 驱动级：终端尺寸变化的真实路径。
///
/// resize 是「替身宿主」永远测不到的一类交互 —— 它由驱动的屏幕尺寸变更事件触发，
/// 会连带触发按新宽度重渲（会话区换行、侧边栏宽度、状态栏布局）。这里断言的是
/// 拉伸之后界面依然完整可读，且不抛异常。
/// </summary>
public sealed class TuiHeadlessResizeTests
{
    private const string ModelName = "test-model";

    [Fact]
    public async Task Resize_ThroughWideAndNarrow_ChromeSurvivesEverySize()
    {
        using var host = TuiHeadlessHost.Start(cols: 100, rows: 30);

        await AssertChromeAtAsync(host, 200, 50);
        await AssertChromeAtAsync(host, 60, 20);
        // 回到原尺寸：状态栏与输入行必须还在，而不是留下上一次的残影。
        await AssertChromeAtAsync(host, 100, 30);
    }

    [Fact]
    public async Task Resize_WhileSessionHasContent_ContentStillRendered()
    {
        const string Reply = "拉伸前的中文回复";
        using var host = TuiHeadlessHost.Start(b => b.Stream(new TuiTextDelta(Reply), new TuiDone(2, 2)));

        await host.SubmitQueryAsync("ask something");
        await host.ResizeAsync(140, 40);
        await host.SettleAsync();

        (await host.ScreenTextAsync()).Should().Contain(Reply, "resize 会按新宽度整体重渲，已提交内容不得丢行");
    }

    private static async Task AssertChromeAtAsync(TuiHeadlessHost host, int cols, int rows)
    {
        await host.ResizeAsync(cols, rows);
        var screen = await host.WaitForScreenAsync(
            text => text.Contains(ModelName, StringComparison.Ordinal),
            $"{cols}x{rows} 下状态栏可见");

        var lines = screen.Split('\n');
        lines.Should().HaveCount(rows, $"屏幕缓冲必须跟着驱动尺寸变成 {rows} 行");
        lines.Should().Contain(
            line => line.Contains(ModelName, StringComparison.Ordinal),
            $"状态栏在 {cols}x{rows} 下必须仍然画得出来");
    }
}
