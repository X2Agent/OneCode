namespace OneCode.App.Tui;

/// <summary>
/// 提问模式（AskUserQuestionTool 向导的文本题输入）：由 ReplShell 经
/// <see cref="EnterTextModeForCurrentQuestion"/> 进入，Enter 提交、
/// Esc/导航键经 <see cref="IInteractionSession"/> 路由。
/// </summary>
public sealed partial class ChatInputView
{
    private bool _isQuestionMode;
    private bool _isLongTextMode;
    private Action<string>? _questionCallback;

    /// <summary>
    /// 进入提问模式 — 接管输入框等待回答（问题文本由向导卡片渲染，
    /// 输入视图只负责输入状态与提交）。Enter 提交、Esc/导航键经
    /// <see cref="IInteractionSession"/> 路由。
    /// </summary>
    public void SetQuestionMode(Action<string> callback, bool longText = false)
    {
        _isQuestionMode = true;
        _isLongTextMode = longText;
        _input.QuestionNavigationEnabled = true;
        _questionCallback = callback;
        _interactionSuspended = false;

        ClearInput();
        _input.ReadOnly = false;

        _placeholderLabel.Visible = false;

        SetNeedsDraw();
        FocusInput();
    }

    /// <summary>
    /// 退出提问模式。
    /// </summary>
    public void ClearQuestionMode()
    {
        _isQuestionMode = false;
        _isLongTextMode = false;
        _input.QuestionNavigationEnabled = false;
        _questionCallback = null;
        ClearInput();
        _input.ReadOnly = _interactionSuspended;
        UpdatePlaceholder();
        SetNeedsDraw();
    }

    /// <summary>
    /// 提交提问模式的回答。
    /// </summary>
    private void SubmitQuestionAnswer()
    {
        if (!_isQuestionMode || _questionCallback is null) return;

        var answer = CurrentText.Trim();
        if (string.IsNullOrEmpty(answer))
        {
            // 空回答视为取消
            _questionCallback("(user cancelled)");
        }
        else
        {
            _questionCallback(answer);
        }
    }

    /// <summary>当前是否处于提问模式。</summary>
    public bool IsQuestionMode => _isQuestionMode;

    /// <summary>当前是否处于长文本提问模式。</summary>
    public bool IsLongTextQuestionMode => _isQuestionMode && _isLongTextMode;
}
