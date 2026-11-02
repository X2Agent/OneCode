namespace OneCode.Tui;

/// <summary>
/// 浮层按钮的统一工厂。
///
/// Terminal.Gui v2 的 <see cref="Button"/> 默认带 <see cref="ShadowStyles.Opaque"/> 阴影：
/// 右侧一列 <c>▖</c>、底部一行 <c>▀</c>、右下角 <c>▝</c>。浮层自绘外框时这些块字符会压在
/// 边框上（底边 <c>╰──╯</c> 被打成 <c>▝▀▀▀</c>、右边 <c>│</c> 被 <c>▖</c> 顶掉），
/// 故浮层按钮一律关掉阴影——层次感由浮层自身的 <c>bg-card</c> 与外框承担，不靠投影。
///
/// 右对齐按钮组也不用 <c>Pos.AnchorEnd(列数)</c> 逐列钉住：该重载把参数读作「距右缘的
/// 偏移」，只有按钮宽度恰好等于该偏移时右缘才对得齐。文案一变（CJK 双宽、状态图标、
/// 加字）两个按钮就互相压叠并越出外框——历史上「是/否」重叠了 5 列、右侧外框被吃掉。
/// <see cref="CreateRightAlignedRow"/> 交给框架的 <see cref="Pos.Align"/> 按实际文本宽度摆位。
/// </summary>
internal static class TuiButtons
{
    /// <summary>容器与外框之间保留的列数（右侧让出外框 1 列 + 留白 1 列）。</summary>
    private const int BorderInset = 2;

    /// <summary>按钮组与外框底边之间保留的行数（外框自身 1 行 + 留白 1 行）。</summary>
    private const int BottomInset = 2;

    /// <summary>创建浮层按钮：无阴影 + 统一配色（正文 / 焦点 <c>bg-active</c> / 热键 accent）。</summary>
    public static Button Create(string text)
    {
        var button = new Button
        {
            Text = text,
            ShadowStyle = null,
        };
        button.SetScheme(TuiStyles.MakeButtonScheme(TuiPalette.FgPrimary, TuiPalette.BgCard, TuiPalette.BgActive));
        return button;
    }

    /// <summary>
    /// 在浮层右下角放置一行右对齐按钮：按传入顺序自左向右，间距由框架的对齐模式提供。
    ///
    /// 用一个铺满浮层内缘、高 1 行的容器承载按钮，按钮统一用 <see cref="Pos.Align"/> 右对齐：
    /// 位置由框架按实际文本宽度算出并自动加间距，因此按钮文案变化（CJK 双宽、状态图标、加字）
    /// 既不会互相压叠，也不会覆盖外框的左右竖线——手写 <c>Pos.AnchorEnd(列数)</c> 正是历史上
    /// 「是/否」重叠 5 列、右侧外框被打掉的根因。
    /// </summary>
    /// <param name="buttons">按钮，顺序即视觉顺序。</param>
    public static View CreateRightAlignedRow(params Button[] buttons)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        if (buttons.Length == 0)
            throw new ArgumentException("按钮组不能为空。", nameof(buttons));

        var container = new View
        {
            X = TuiSpacing.Xs,
            Width = Dim.Fill(BorderInset + TuiSpacing.Xs),
            Y = Pos.AnchorEnd(BottomInset),
            Height = 1,
            CanFocus = true,
            TabStop = TabBehavior.TabGroup,
        };

        // 同一 groupId 表示这组按钮作为一个整体参与右对齐。
        foreach (var button in buttons)
        {
            button.X = Pos.Align(Alignment.End, groupId: ButtonGroupId);
            button.Y = 0;
            container.Add(button);
        }

        return container;
    }

    /// <summary>按钮组的对齐分组标识；全部浮层共用一组即可（同一父视图内各自成组）。</summary>
    private const int ButtonGroupId = 1;
}
