using System.Reflection;
using NSubstitute;
using OneCode.App.Tui;
using OneCode.Core.Mcp;
using Terminal.Gui.App;

namespace OneCode.Tests;

/// <summary>
/// AgentStatusBar 左侧分段的单一事实源守护：
/// 分段顺序与可丢弃标记、空间不足时的尾部丢弃顺序，以及
/// 「绘制与估宽共用同一列表」——杜绝两套宽度算法各自维护魔数导致的错位。
/// </summary>
public sealed class AgentStatusBarTests
{
    private static AgentStatusBar CreateBar()
    {
        var app = Substitute.For<IApplication>();
        app.AddTimeout(Arg.Any<TimeSpan>(), Arg.Any<Func<bool>>()).Returns(true);
        return new AgentStatusBar(app, new WorkingModeController());
    }

    private static string Joined(IEnumerable<AgentStatusBar.LeftItem> items)
        => string.Concat(items.Select(item => item.Separator + item.Text));

    [Fact]
    public void BuildLeftItems_ModelIsAlwaysPresentAndNeverDroppable()
    {
        var bar = CreateBar();
        bar.SetModel("claude-3-7-sonnet-thought");

        var items = bar.BuildLeftItems();

        items.Should().Contain(
            item => item.Text == "claude-3-7-sonnet-thought" && !item.Droppable,
            "模型名必须原样显示且永不丢弃（超宽由绘制侧按列截断兜底）");
    }

    [Fact]
    public void BuildLeftItems_BusyPutsActivityBeforeModel()
    {
        var bar = CreateBar();
        bar.SetBusy(true);
        bar.SetActivity("思考中");

        var items = bar.BuildLeftItems();

        items[0].Text.Should().Contain("思考中");
        items[0].Droppable.Should().BeFalse();
        items[1].Text.Should().Be("Opus");
    }

    [Fact]
    public void BuildLeftItems_LongActivityIsCappedToActivityMaxWidth()
    {
        var bar = CreateBar();
        bar.SetBusy(true);
        bar.SetActivity(new string('x', 200));

        var segment = bar.BuildLeftItems()[0].Text; // spinner + 空格 + activity
        var activityWidth = TextWidthHelper.GetDisplayWidth(segment) - 2;

        activityWidth.Should().BeLessThanOrEqualTo(AgentStatusBar.ActivityMaxWidth);
    }

    [Fact]
    public void BuildLeftItems_McpConnected_KeepsSeparatorBeforeToolCount()
    {
        var bar = CreateBar();
        bar.SetMcpStatus(new McpConnectionSummary(Expected: 3, Connected: 3, Connecting: 0, Failed: 0, ToolCount: 5));

        var text = Joined(bar.BuildLeftItems());

        text.Should().Contain("MCP: 3s");
        text.Should().Contain(" · 5t", "工具数必须带分隔符，不得渲染成 'MCP: 3s5t'");
        text.Should().NotContain("3s5t");
    }

    [Fact]
    public void BuildLeftItems_StatusSegmentsAreDroppable()
    {
        var bar = CreateBar();
        bar.SetLspStatus(2, 1, 1);
        bar.SetMcpStatus(new McpConnectionSummary(3, 3, 0, 1, 5));

        var items = bar.BuildLeftItems();

        items.Where(item => item.Text.Contains("LSP") || item.Text.Contains("MCP") || item.Text.Contains("失败"))
            .Should().OnlyContain(item => item.Droppable);
    }

    [Fact]
    public void Fit_DropsDroppableItemsFromTail()
    {
        var bar = CreateBar();
        bar.SetLspStatus(2, 0, 0);
        bar.SetMcpStatus(new McpConnectionSummary(3, 3, 0, 0, 5));

        var all = bar.BuildLeftItems();
        var budget = AgentStatusBar.MeasureWidth(all.Take(2).ToList());
        var fitted = AgentStatusBar.Fit(all, budget);

        fitted.Should().HaveCount(2);
        AgentStatusBar.MeasureWidth(fitted).Should().BeLessThanOrEqualTo(budget);
    }

    [Fact]
    public void Fit_NeverDropsCoreItems()
    {
        var bar = CreateBar();
        bar.SetBusy(true);
        bar.SetActivity("思考中");
        bar.SetLspStatus(2, 0, 0);

        var fitted = AgentStatusBar.Fit(bar.BuildLeftItems(), maxWidth: 1);

        fitted.Should().HaveCount(2, "必显条目（activity + 模型名）不可丢弃");
        fitted.Should().OnlyContain(item => !item.Droppable);
    }

    [Fact]
    public void Fit_KeepsEverythingWhenItFits()
    {
        var bar = CreateBar();
        bar.SetLspStatus(2, 0, 0);
        bar.SetMcpStatus(new McpConnectionSummary(3, 3, 0, 0, 5));

        var all = bar.BuildLeftItems();
        var fitted = AgentStatusBar.Fit(all, AgentStatusBar.MeasureWidth(all));

        fitted.Should().HaveCount(all.Count);
    }

    [Fact]
    public void MeasureWidth_EqualsRenderedSegmentSum()
    {
        var bar = CreateBar();
        bar.SetBusy(true);
        bar.SetActivity("思考中");
        bar.SetLspStatus(2, 1, 1);
        bar.SetMcpStatus(new McpConnectionSummary(3, 3, 0, 1, 5));

        var items = bar.BuildLeftItems();
        var expected = 1 + items.Sum(item =>
            TextWidthHelper.GetDisplayWidth(item.Separator) + TextWidthHelper.GetDisplayWidth(item.Text));

        AgentStatusBar.MeasureWidth(items).Should().Be(expected);
    }

    /// <summary>
    /// 回归防护：本次审计确认「硬编码模型名映射表 + 5 级降级 + 两套宽度算法」
    /// 均为过度设计，已替换为「原样显示 + 按列截断 + 单一宽度来源」。
    /// </summary>
    [Fact]
    public void StatusBar_NoLongerCarriesLegacyShorteningOrDegradationLadder()
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        typeof(AgentStatusBar).GetMethod("ShortenModelName", Any)
            .Should().BeNull("硬编码模型名映射表已删除，改由按列截断兜底");
        typeof(AgentStatusBar).GetMethod("DetermineDegradationLevel", Any)
            .Should().BeNull("5 级降级已简化为按优先级尾部丢弃");
        typeof(AgentStatusBar).GetMethod("EstimateLeftWidth", Any)
            .Should().BeNull("两套宽度算法已统一为 BuildLeftItems + MeasureWidth");
        typeof(AgentStatusBar).GetMethod("DigitLength", Any)
            .Should().BeNull("魔数宽度估算已删除");
    }
}
