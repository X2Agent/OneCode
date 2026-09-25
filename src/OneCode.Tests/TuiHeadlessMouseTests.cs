using OneCode.App.Tui;
using OneCode.Tests.TestSupport.Tui;

namespace OneCode.Tests;

/// <summary>
/// L3 驱动级鼠标交互：把点击注入真实驱动，命中会话区里的工具行，验证「点击展开」真的换了屏。
///
/// 这是 L2 够不到的一段：替身宿主里没有鼠标命中测试，也没有坐标换算 ——
/// 屏幕行号 → 视口行号 → 行索引这三步只要错一步，用户点到的那一行就不是展开的那一行，
/// 而这类错位在结构断言里完全看不出来，只有把点击送进真实驱动、再看屏幕才暴露。
/// </summary>
public sealed class TuiHeadlessMouseTests
{
    private const string ToolResultMarker = "展开后才可见的工具输出标记";
    private const string ToolInput = "{\"path\":\"app.json\"}";

    /// <summary>Terminal.Gui MouseInterpreter 的双击窗口默认 500ms，取 700ms 确保两次点击被当成独立事件。</summary>
    private const int ClickGapMs = 700;

    private const string TopMarker = "滚轮测试首行标记";
    private const string TailMarker = "滚轮测试末行标记";

    private static readonly string[] FillerLines =
    [
        .. Enumerable.Range(1, 60).Select(i => $"填充第 {i} 行：滚轮需要足够长的会话内容才有的可滚"),
        TailMarker,
    ];

    [Fact]
    public async Task ClickToolRow_InjectedThroughRealDriver_ExpandsToolDetail()
    {
        using var host = TuiHeadlessHost.Start(b => b.Stream(
            new TuiToolStart("t-1", "read_file", ToolInput: ToolInput),
            new TuiToolDone("read_file", IsError: false, Result: ToolResultMarker, ToolInput: ToolInput, ToolId: "t-1"),
            new TuiDone(10, 20)));

        await host.SubmitQueryAsync("读一下配置");
        await host.SettleAsync();

        var collapsed = await host.ScreenLinesAsync();
        var collapsedRow = FindRow(collapsed, "read_file");
        collapsed.Should().NotContain(
            line => line.Contains(ToolResultMarker, StringComparison.Ordinal),
            "折叠态只画单行摘要，详情文本此时不该在屏幕上");

        await host.ClickAsync(6, collapsedRow);

        var screen = await host.WaitForScreenAsync(
            text => text.Contains(ToolResultMarker, StringComparison.Ordinal),
            "点击工具行后展开详情");

        screen.Should().Contain(ToolResultMarker, "左键点中工具行必须展开它的完整输出");
        screen.Should().Contain("Args: ", "展开详情由 Args 与输出两段组成");

        var expandedRow = FindRow(await host.ScreenLinesAsync(), "read_file");
        expandedRow.Should().Be(collapsedRow, "展开是在原行下方插入详情，工具行本身不能位移");
    }

    [Fact]
    public async Task ClickToolRowTwice_InjectedThroughRealDriver_CollapsesBackToSingleLine()
    {
        using var host = TuiHeadlessHost.Start(b => b.Stream(
            new TuiToolStart("t-1", "read_file", ToolInput: ToolInput),
            new TuiToolDone("read_file", IsError: false, Result: ToolResultMarker, ToolInput: ToolInput, ToolId: "t-1"),
            new TuiDone(10, 20)));

        await host.SubmitQueryAsync("读一下配置");
        await host.SettleAsync();
        var collapsedRow = FindRow(await host.ScreenLinesAsync(), "read_file");

        await host.ClickAsync(6, collapsedRow);
        await host.WaitForScreenAsync(
            text => text.Contains(ToolResultMarker, StringComparison.Ordinal),
            "第一次点击展开");

        // 真实驱动的 MouseInterpreter 有 500ms 双击窗口：窗口内同坐标的第二次点击会被合成为双击，
        // 不再派发可命中的 LeftButtonClicked（实测：连点两次只切换一次）。
        // 这里要模拟用户两次独立点击，必须跨过这个窗口。
        await Task.Delay(ClickGapMs, TestContext.Current.CancellationToken);
        await host.ClickAsync(6, collapsedRow);
        await host.WaitForScreenAsync(
            text => !text.Contains(ToolResultMarker, StringComparison.Ordinal),
            "第二次点击收回详情");

        var screen = await host.ScreenTextAsync();
        screen.Should().NotContain(ToolResultMarker, "再次点击同一行必须折回单行摘要");
        screen.Should().Contain("read_file", "折叠回去不能把工具行本身弄丢");
    }

    [Fact]
    public async Task WheelUpOverTranscript_InjectedThroughRealDriver_ScrollsBackToEarlierContent()
    {
        var body = string.Join('\n', new[] { TopMarker }.Concat(FillerLines));
        using var host = TuiHeadlessHost.Start(b => b.Stream(new TuiTextDelta(body), new TuiDone(80, 5)));

        await host.SubmitQueryAsync("滚一下");
        await host.SettleAsync();

        var tail = await host.ScreenTextAsync();
        tail.Should().Contain(TailMarker, "内容超出视口时默认贴底，最后一行必须在屏幕上");
        tail.Should().NotContain(TopMarker, "首行已滚出视口——否则这个用例证明不了滚轮做了任何事");

        for (var i = 0; i < 20; i++)
            await host.WheelAsync(20, 5, up: true);

        var screen = await host.WaitForScreenAsync(
            text => text.Contains(TopMarker, StringComparison.Ordinal),
            "滚轮上滚回看首行");

        screen.Should().Contain(TopMarker, "滚轮必须真的改变视口起点，而不只是改状态位");
        TuiHeadlessHost.DisplayColumns(screen.Split('\n').Single(l => l.Contains(TopMarker, StringComparison.Ordinal)))
            .Should().BeLessThanOrEqualTo(host.Cols, "上滚后回看的老内容同样不能溢出屏幕宽度");
    }

    private static int FindRow(IReadOnlyList<string> lines, string needle)
    {
        var row = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Contains(needle, StringComparison.Ordinal)) continue;
            row = i;
            break;
        }

        row.Should().BeGreaterThanOrEqualTo(0, $"屏幕上必须能找到包含「{needle}」的行，否则点击坐标无从推导");
        return row;
    }
}
