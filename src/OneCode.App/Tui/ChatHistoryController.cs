namespace OneCode.App.Tui;

/// <summary>
/// Manages in-session chat input history navigation (Up/Down arrows) for <see cref="ChatInputView"/>.
/// The history source is the current conversation's user messages, provided via delegate
/// so this class stays decoupled from <c>ISessionManager</c>.
/// </summary>
internal sealed class ChatHistoryController
{
    private readonly Func<IReadOnlyList<string>> _historyProvider;
    private int _historyIndex = -1;
    private string _draftInput = string.Empty;

    public ChatHistoryController(Func<IReadOnlyList<string>> historyProvider)
    {
        _historyProvider = historyProvider;
    }

    public IReadOnlyList<string> History => _historyProvider();

    /// <summary>
    /// Navigate to previous (older) history entry.
    /// Returns the text to put into the input, or null if no change.
    /// </summary>
    public string? NavigateUp(string currentInput)
    {
        var history = _historyProvider();
        if (history.Count == 0) return null;

        if (_historyIndex < 0)
        {
            _draftInput = currentInput;
            _historyIndex = history.Count - 1;
        }
        else if (_historyIndex > 0)
        {
            _historyIndex--;
        }

        return history[_historyIndex];
    }

    /// <summary>
    /// Navigate to next (newer) history entry.
    /// Returns the text to put into the input, or null if no change.
    /// </summary>
    public string? NavigateDown()
    {
        var history = _historyProvider();
        if (_historyIndex < 0) return null;

        if (_historyIndex < history.Count - 1)
        {
            _historyIndex++;
            return history[_historyIndex];
        }

        _historyIndex = -1;
        return _draftInput;
    }

    /// <summary>
    /// Resets navigation state after submission so the next Up starts from the latest entry.
    /// </summary>
    public void ResetNavigation()
    {
        _historyIndex = -1;
        _draftInput = string.Empty;
    }

    /// <summary>
    /// 无条件召回最后一条用户消息（Ctrl+Up）。
    /// 与 NavigateUp 不同，不受当前光标行位置限制。
    /// 返回最后一条消息文本，或 null 当历史为空时。
    /// </summary>
    public string? RecallLast()
    {
        var history = _historyProvider();
        if (history.Count == 0) return null;
        _historyIndex = history.Count - 1;
        return history[_historyIndex];
    }
}
