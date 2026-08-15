namespace OneCode.App.Tui;

/// <summary>
/// <see cref="ChatInputView"/> 的视图构建与绘制几何：子控件创建、事件接线、
/// 以及随内容行数自适应的动态高度布局。
/// </summary>
public sealed partial class ChatInputView
{
    private void BuildViews()
    {
        X = 0;
        Width = Dim.Fill();
        Height = 1 + MinVisibleLines;
        SetScheme(TuiTheme.ChatInput);
        // Focus belongs exclusively to ChatTextEditor.Editor; the wrapper only
        // keeps the ancestor focus path valid and must not become a tab target.
        CanFocus = true;
        TabStop = TabBehavior.NoStop;

        _separatorLabel = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
            // 分隔线：300 字符足够覆盖大多数终端宽度（最多约 300 列）。
            Text = new string((char)0x2500, 300),
            CanFocus = false,
        };

        _input = new ChatTextEditor
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = Dim.Fill() - 1,
            CanFocus = true,
        };
        _input.KeyDownEvent += OnInputKeyPress;
        _input.ContentsChanged += OnInputTextChanged;

        CycleModeRequested += () => _modeController.CycleMode();
        ToggleStrategyRequested += () => _modeController.ToggleStrategy();

        // Fallback for terminals/drivers where IApplication.Paste doesn't fire
        // (e.g. ConPTY may strip bracketed-paste markers and deliver text as
        // regular input). ChatTextEditor detects large pastes by line-count
        // jumps in ContentsChanged and raises LargeTextPasted.
        //
        // Deferred via _app.Invoke: LargeTextPasted fires synchronously inside
        // OnDocumentChanged (during Editor's TextChanged event). If we call
        // HandlePastedText inline, Editor may continue inserting raw text
        // after we set _input.Text to the collapsed summary, overriding it.
        // Deferring to the next UI cycle ensures Editor has fully completed
        // its paste processing before we collapse.
        _input.LargeTextPasted += text => _app.Invoke(() => HandlePastedText(text, isFullText: true));

        _completionList = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = false,
        };

        _completionFrame = new FrameView
        {
            X = 0,
            Y = -15,
            Width = Dim.Fill(),
            Height = 16,
            Title = " commands (Tab 切换 · Enter 选择 · Esc 关闭) ",
            CanFocus = false,
        };
        _completionFrame.Add(_completionList);

        _placeholderLabel = new Label
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = 1,
            Text = "",
            CanFocus = false,
            Visible = false,
        };

        Add(_separatorLabel);
        Add(_placeholderLabel);
        Add(_input);
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        if (width <= 0)
            return false;

        var inputLines = Math.Clamp(_input.LineCount, MinVisibleLines, ChatTextEditor.MaxVisibleLines);
        var totalHeight = 1 + inputLines;
        if (_lastHeight != totalHeight || _lastBottomOffset != BottomOffset)
        {
            _lastHeight = totalHeight;
            _lastBottomOffset = BottomOffset;
            Height = totalHeight;
            Y = Pos.AnchorEnd(totalHeight + BottomOffset);
            SetNeedsLayout();
        }

        _separatorLabel.Width = width;
        var editorWidth = Math.Max(1, width - 2);
        _input.X = 1;
        _input.Y = 1;
        _input.Width = editorWidth;
        _input.Height = inputLines;
        _placeholderLabel.X = 1;
        _placeholderLabel.Y = 1;
        _placeholderLabel.Width = editorWidth;

        return base.OnDrawingContent(context);
    }
}
