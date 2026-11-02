using OneCode.Tests.TestSupport.Tui;
using Terminal.Gui.Text;

namespace OneCode.Tests;

/// <summary>
/// 浮层外框完整性的 L3 驱动级回归测试：从真实驱动的屏幕缓冲逐格读回，
/// 断言外框四边闭合、按钮组完全落在外框以内。
///
/// 两类缺陷只在"读屏幕"时暴露，结构断言（Frame / Width 数值）会看起来一切正常：
/// 1. <see cref="Terminal.Gui.Views.Button"/> 默认带 <c>Opaque</c> 阴影，块字符 <c>▝▀▀▀</c>
///    压在浮层底边、<c>▖</c> 压在右边，把闭合的圆角外框打出缺口；
/// 2. 按钮用 <c>Pos.AnchorEnd(列数)</c> 逐列钉死，CJK 双宽下两个按钮互相压叠并把外框顶掉。
/// </summary>
public sealed class OverlayFrameIntegrityTests
{
    public static TheoryData<string, int, int> Overlays => new()
    {
        { "trust", 100, 30 },
        { "trust-narrow", 60, 26 },
        { "resume", 100, 34 },
        { "settings", 110, 40 },
        { "settings-narrow", 60, 30 },
        { "mcp", 110, 40 },
        { "review", 110, 30 },
        { "keybindings", 100, 30 },
        { "diffdetail", 110, 34 },
    };

    [Theory]
    [MemberData(nameof(Overlays))]
    public async Task Overlay_FrameIsClosedAndButtonsStayInside(string name, int cols, int rows)
    {
        using var host = await OpenAsync(name, cols, rows);
        var cells = await ReadCellsAsync(host);
        var (left, right, top, bottom) = LocateFrame(cells);

        // 底边必须整段连续：缺一格就说明有东西（按钮阴影 / 行内容）压在外框上。
        var brokenBottom = Enumerable.Range(left + 1, right - left - 1)
            .Where(col => cells[bottom][col] != "─")
            .ToList();
        brokenBottom.Should().BeEmpty(
            $"「{name}」底边外框必须连续，缺格列：{string.Join(",", brokenBottom)}");

        // 左右竖线在每一中间行都必须存在，且外框以外不得有内容溢出。
        for (var row = top + 1; row < bottom; row++)
        {
            (cells[row][left] == "│" || cells[row][left] == "├")
                .Should().BeTrue($"「{name}」第 {row} 行左边框缺失");
            (cells[row][right] == "│" || cells[row][right] == "┤")
                .Should().BeTrue($"「{name}」第 {row} 行右边框缺失");
            RowText(cells[row], right + 1).Trim()
                .Should().BeEmpty($"「{name}」第 {row} 行内容溢出到外框之外");
        }
    }

    [Theory]
    [InlineData(100)]
    [InlineData(60)]
    public async Task TrustButtons_RenderCompleteAndInsideFrame(int cols)
    {
        using var host = await OpenAsync("trust", cols, 30);
        var cells = await ReadCellsAsync(host);
        var (left, right, top, bottom) = LocateFrame(cells);

        var buttonRow = Enumerable.Range(top + 1, bottom - top - 1)
            .Single(row => RowText(cells[row], 0).Contains("我信任此文件", StringComparison.Ordinal));
        var text = RowText(cells[buttonRow], 0);

        // 两个按钮的文案必须完整可见：旧实现下 CJK 双宽把两个按钮挤到重叠，
        // 「是」的尾巴被「否」盖掉。
        text.Should().Contain("是，我信任此文件夹", "「是」按钮文案必须完整渲染，不能被相邻按钮遮盖");
        text.Should().Contain("否，退出", "「否」按钮文案必须完整渲染");

        // 按钮行两侧必须仍是外框竖线。
        cells[buttonRow][left].Should().Be("│", "按钮行左侧仍须有外框竖线");
        cells[buttonRow][right].Should().Be("│", "按钮行右侧仍须有外框竖线");
    }

    /// <summary>
    /// 直接放在浮层上（无裁剪容器）的阴影按钮会打穿底边——这是用户截图里的原始形态。
    /// 断言浮层按钮一律无阴影，且渲染后底边整段连续。
    /// </summary>
    [Fact]
    public async Task OverlayButtons_CarryNoShadow_SoFrameStaysClosed()
    {
        using var host = await OpenAsync("trust", 100, 30);
        var cells = await ReadCellsAsync(host);
        var (left, right, top, bottom) = LocateFrame(cells);

        RowText(cells[bottom], left + 1)[..(right - left - 1)]
            .Should().MatchRegex("^─+$", "按钮一旦带 Opaque 阴影，底边会被 ▝▀▀▀ 打成缺口");
    }

    private static async Task<TuiHeadlessHost> OpenAsync(string name, int cols, int rows)
    {
        var host = TuiHeadlessHost.Start(cols: cols, rows: rows);
        try
        {
            await host.OnLoopAsync(() =>
            {
                var shell = host.Toplevel.SubViews.OfType<OneCode.Tui.ReplShell>().Single();
                shell.Overlays.Push(Create(name));
                return true;
            });
            await host.SettleAsync();
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private static Task<string[][]> ReadCellsAsync(TuiHeadlessHost host) => host.OnLoopAsync(() =>
    {
        var driver = host.App.Driver!;
        var contents = driver.Contents!;
        var height = driver.Screen.Height;
        var width = driver.Screen.Width;
        var cells = new string[height][];
        for (var row = 0; row < height; row++)
        {
            cells[row] = new string[width];
            for (var col = 0; col < width; col++)
            {
                var grapheme = contents[row, col].Grapheme;
                cells[row][col] = string.IsNullOrEmpty(grapheme) ? " " : grapheme;
            }
        }
        return cells;
    });

    /// <summary>
    /// 从指定列起拼接该行文本。全角字符在缓冲区的第二格是续格（Grapheme 为空），
    /// 必须跳过续格，否则拼出「流 式 中 文」这种带缝文本，<c>Contains</c> 永远匹配不上。
    /// </summary>
    private static string RowText(string[] row, int startColumn)
    {
        var builder = new System.Text.StringBuilder(row.Length);
        for (var col = startColumn; col < row.Length; col++)
        {
            var grapheme = row[col];
            builder.Append(grapheme);
            if (grapheme.Length > 0 && grapheme.GetColumns(ignoreLessThanZero: true) == 2)
                col++;
        }
        return builder.ToString();
    }

    private static (int Left, int Right, int Top, int Bottom) LocateFrame(string[][] cells)
    {
        var top = Array.FindIndex(cells, line => Array.IndexOf(line, "╭") >= 0);
        var bottom = Array.FindLastIndex(cells, line => Array.IndexOf(line, "╰") >= 0);
        top.Should().BeGreaterThanOrEqualTo(0, "屏幕上必须出现浮层顶边框");
        bottom.Should().BeGreaterThan(top, "浮层底边框必须在顶边框之下");
        var left = Array.IndexOf(cells[top], "╭");
        var right = Array.LastIndexOf(cells[top], "╮");
        right.Should().BeGreaterThan(left, "顶边框必须由 ╭ … ╮ 闭合");
        return (left, right, top, bottom);
    }

    private static Terminal.Gui.ViewBase.View Create(string name) => name switch
    {
        "trust" or "trust-narrow" => new OneCode.Tui.TrustOverlay(@"C:\Users\mayue", isHomeDir: true),
        "resume" => new OneCode.Tui.ResumeChooserOverlay([
            new OneCode.Tui.SessionEntry("s1", "会话一", "gpt-5", "build", 3, DateTimeOffset.UtcNow),
        ]),
        "settings" or "settings-narrow" => new OneCode.Tui.SettingsOverlay(
            OneCode.Core.Config.ConfigSnapshot.FromEffective(new OneCode.Core.Config.AppSettings(new Dictionary<string, object?>
            {
                ["provider"] = "openai",
                ["model"] = "gpt-5.6",
            })),
            projectScopeAvailable: true),
        "mcp" => new OneCode.Tui.McpConfigOverlay([
            new OneCode.App.Services.Mcp.McpConfigServerEntry("context7", false, true, null, ["resolve", "get-docs", "search"]),
        ]),
        "review" => new OneCode.Tui.ReviewOverlay([
            new OneCode.Core.Commands.ReviewFileEntry("src/a.cs", 10, 2, "M"),
        ]),
        "keybindings" => new OneCode.Tui.KeybindingsOverlay(
            [new OneCode.Core.Keybindings.KeybindingView("chat", "Ctrl+K", "chat:clear", OneCode.Core.Keybindings.KeybindingSource.Default, "清空输入")],
            []),
        "diffdetail" => new OneCode.Tui.DiffDetailOverlay(null!, "src/a.cs", "--- a\n+++ b\n"),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "未知浮层用例。"),
    };
}


