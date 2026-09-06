using OneCode.App.Tui;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace OneCode.Tests;

/// <summary>
/// CenteredOverlay 初始聚焦语义的无头（headless）回归测试。
/// 背景缺陷：InitialFocusView 缺省（或其 SetFocus 被否决）时旧实现回退到
/// overlay 根部 SetFocus()，真实 App 下 RestoreFocus/AdvanceFocus 下沉链
/// 失败会让焦点滞留在 FrameView 边框/标题上（红框高亮）；容器自身没有
/// 按键绑定，←/→ 随后失控冒泡到 ReplShell 的会话记录导航。
/// </summary>
public sealed class CenteredOverlayTests
{
    /// <summary>不覆写 InitialFocusView 的最小 overlay：FrameView 包裹一个可聚焦列表。</summary>
    private sealed class PlainOverlay : CenteredOverlay
    {
        public PlainOverlay() : base("plain")
        {
            FrameView frame = new()
            {
                X = 1,
                Y = 2,
                Width = 10,
                Height = 5,
                Title = " pane ",
            };
            frame.Add(new ListView { Width = Dim.Fill(), Height = Dim.Fill() });
            Add(frame);
        }
    }

    [Fact]
    public void FocusInitialView_WithoutOverride_LandsOnFirstFocusableLeafNotFrame()
    {
        var overlay = new PlainOverlay();
        overlay.Layout();

        overlay.FocusInitialView();

        // 焦点必须落到叶子控件（FrameView 内的列表），绝不是 overlay 根部
        // 或 FrameView 容器——真实 App 下容器滞焦会导致按键冒泡失控。
        overlay.MostFocused.Should().BeOfType<ListView>();
    }
}
