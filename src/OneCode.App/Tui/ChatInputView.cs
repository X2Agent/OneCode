using OneCode.Infrastructure.Media;
using OneCode.Core.Keybindings;
using System.Collections.ObjectModel;

namespace OneCode.App.Tui;

/// <summary>
/// Complete chat input region: separator, prompt state, multiline editor,
/// completion popup coordination, paste handling, and dynamic bottom layout.
/// </summary>
public sealed partial class ChatInputView : View
{
    private const int MinVisibleLines = 3;
    public const int MaxHeight = 1 + ChatTextEditor.MaxVisibleLines;

    private readonly IApplication _app;
    private readonly WorkingModeController _modeController;
    private readonly IReadOnlyList<SlashCommandEntry> _allCommands;

    private readonly Label _separatorLabel;
    private readonly ChatTextEditor _input;
    private int _lastHeight = 1 + MinVisibleLines;
    private int _lastBottomOffset;
    private readonly ListView _completionList;
    private readonly FrameView _completionFrame;

    private bool _isBusy;
    private bool _interactionSuspended;

    private ObservableCollection<string> _completionItems = [];

    // Paste collapse state
    // When users paste long multi-line text, store the real content and
    // display a one-line summary "[Pasted text #N +L lines]" instead.
    private string? _pastedText;
    private int _pasteCount;

    // Image attachment state
    // Pasted images are saved to temp files; the input shows [Image #N] tags
    // and the real file paths are stored for submission.
    private int _imageCount;
    private readonly Dictionary<int, string> _pendingImages = new();

    private readonly Label _placeholderLabel;
    private IReadOnlyList<string> _suggestions = [];
    private int _suggestionIndex;

    private readonly ChatCompletionController _completion;
    private readonly ChatHistoryController _historyCtrl;
    private readonly SmartPasteHandler _smartPaste;
    private readonly OneCode.Core.IO.IClipboardService? _clipboard;

    // Multimodal support
    // Set by OneCodeToplevel after construction. When false, pasted images
    // are rejected with a system notification instead of inserting [Image #N].
    internal Func<bool>? IsMultimodalSupported { get; set; }
    internal ImagePipeline? ImagePipeline { get; set; }

    // Keybinding system integration — always non-null: caller-provided or default bindings.
    private readonly KeybindingResolver _keyResolver;
    private readonly KeybindingContextManager _keyContextManager;

    /// <summary>
    /// Extra offset from the bottom for the session context bar and configured gap.
    /// </summary>
    public int BottomOffset { get; set; }

    // Guard flag: suppress TextChanged-triggered completion during programmatic text changes
    private bool _suppressCompletion;

    public event Action<string>? Submitted;
    public event Action? QuitRequested;
    public event Action? ImagePasteRequested;
    /// <summary>Raised when the user presses Tab in a non-completion context to cycle the working mode.</summary>
    public event Action? CycleModeRequested;

    /// <summary>
    /// Raised when an image paste is rejected because the current model doesn't
    /// support multimodal input. The caller should show a visible error message.
    /// </summary>
    public event Action? ImagePasteRejected;

    /// <summary>
    /// 在 TEAM 模式下切换 Magentic ↔ GroupChat 策略（Shift+Tab）。
    /// 由本视图直接桥接到 WorkingModeController.ToggleStrategy()。
    /// </summary>
    public event Action? ToggleStrategyRequested;

    /// <summary>
    /// 在 TEAM 模式下循环切换已注册团队（Ctrl+Shift+T）。
    /// 调用方（OneCodeToplevel）将其桥接到 TuiContext.CycleTeam 回调，
    /// 并刷新 AgentStatusBar 显示的团队名。
    /// </summary>
    public event Action? CycleTeamRequested;

    /// <summary>
    /// Raised when a key is pressed while interaction is suspended (e.g. InlineSelector
    /// for permission prompts is active). Editor consumes all keystrokes so ReplShell.OnKeyDown
    /// never fires — this event lets ReplShell forward keys to the active InlineSelector.
    /// </summary>
    public event Action<Key>? InteractionSuspendedKeyForwarded;

    /// <summary>提问模式下回到上一题。</summary>
    public event Action? QuestionPreviousRequested;

    /// <summary>提问模式下取消当前向导。</summary>
    public event Action? QuestionCancelRequested;

    /// <summary>长文本提问模式下提交或回到上一题；参数 true 表示上一题。</summary>
    public event Action<bool>? QuestionLongTextNavigationRequested;

    /// <summary>文本题中使用 Alt+Left/Alt+Right 在问题之间导航；参数 true 表示上一题。</summary>
    public event Action<bool>? QuestionTextNavigationRequested;

    // Global shortcut forwarding
    // These events let global shortcuts fire even while the prompt has focus,
    // because Editor otherwise consumes all keystrokes and ReplShell.OnKeyDown
    // never sees them.

    /// <summary>
    /// 取消当前正在运行的 agent/query，但不退出应用（Ctrl+X Ctrl+K）。
    /// 调用方（OneCodeToplevel）将其桥接到 <c>_queryCts.Cancel()</c>。
    /// </summary>
    public event Action? InterruptRequested;

    /// <summary>对话区向上滚动（Shift+Up / Ctrl+PgUp）。</summary>
    public event Action? ScrollUpRequested;

    /// <summary>对话区向下滚动（Shift+Down / Ctrl+PgDn）。</summary>
    public event Action? ScrollDownRequested;

    /// <summary>对话区页级向上滚动（PageUp）。</summary>
    public event Action? PageUpRequested;

    /// <summary>对话区页级向下滚动（PageDown）。</summary>
    public event Action? PageDownRequested;

    public event Action<bool, int>? CompletionStateChanged;
    public FrameView CompletionFrame => _completionFrame;
    public int SelectedSuggestionIndex => _completionList.SelectedItem ?? -1;
    public IReadOnlyList<SlashCommandEntry> FilteredCommands => _completion.FilteredCommands;
    public bool IsCompletionActive => _completion.IsCompletionActive;

    public ChatInputView(
        IApplication app,
        WorkingModeController modeController,
        IReadOnlyList<SlashCommandEntry> commands,
        Func<IReadOnlyCollection<string>> toolNameProvider,
        KeybindingResolver? keyResolver = null,
        KeybindingContextManager? keyContextManager = null,
        OneCode.Core.IO.IClipboardService? clipboard = null,
        Func<IReadOnlyList<string>>? historyProvider = null)
    {
        _app = app;
        _modeController = modeController;
        _allCommands = commands;
        // Fall back to default bindings so key dispatch always works (tests, standalone construction).
        _keyResolver = keyResolver ?? new KeybindingResolver();
        _keyResolver.SetBindings([.. KeybindingDefaults.GetDefaultParsedBindings()]);
        _keyContextManager = keyContextManager ?? new KeybindingContextManager();
        _clipboard = clipboard;

        var typeaheadEngine = new TypeaheadCompletionEngine(commands, Environment.CurrentDirectory, toolNameProvider);

        _completion = new ChatCompletionController(commands, typeaheadEngine);
        // History source: current conversation's user prompts. Falls back to empty
        // list for tests/standalone construction without a session manager.
        _historyCtrl = new ChatHistoryController(historyProvider ?? (() => []));
        _smartPaste = new SmartPasteHandler(app, clipboard);

        // Terminal.Gui v2 enables bracketed paste mode automatically.
        // Pasted text is delivered via IApplication.Paste event, NOT via
        // KeyDown key events. Subscribe here to intercept bracketed-paste
        // deliveries and route them through HandlePastedText (which applies
        // multi-line folding, image detection, and path expansion).
        // NOTE: Ctrl+V initiated paste is handled separately in OnInputKeyPress
        // via KeybindingResolver — this subscription only covers the
        // bracketed-paste channel (e.g., terminal menu paste).
        _app.Paste += OnApplicationPaste;

        _completion.CompletionStateChanged += (visible, height) =>
        {
            CompletionStateChanged?.Invoke(visible, height);
            // 激活/取消 ContextAutocomplete：补全菜单可见时，up/down 解析为
            // autocomplete:previous/next（覆盖 ContextChat 的 history:previous/next）。
            // 这让用户可以通过 keybindings.json 重映射补全菜单的导航键。
            if (visible)
                _keyContextManager.PushContext(KeybindingDefaults.ContextAutocomplete);
            else
                _keyContextManager.PopContext(KeybindingDefaults.ContextAutocomplete);

            if (!visible)
            {
                _completionFrame.Visible = false;
                _completionFrame.SetNeedsDraw();
                SetNeedsDraw();
                return;
            }
            _completionFrame.Visible = true;
            if (_completion.CurrentDisplayItems is { } items)
            {
                _completionItems = new ObservableCollection<string>(items);
                _completionList.SetSource(_completionItems);
                _completionList.SelectedItem = _completion.SelectedIndex;
            }
            _completionFrame.SetNeedsDraw();
        };

        _smartPaste.ImagePasteRequested += () => ImagePasteRequested?.Invoke();

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

    /// <summary>
    /// Replaces the command list used for slash-completion at runtime.
    /// Call after dynamic commands (skills, MCP) are loaded or refreshed.
    /// </summary>
    public void UpdateCommands(IReadOnlyList<SlashCommandEntry> commands)
        => _completion.UpdateCommands(commands);

    public void SetBusy(bool busy)
    {
        _isBusy = busy;
        // Busy 状态只影响建议占位符；运行阶段统一显示在 AgentStatusBar。
        // 输入保持可编辑，用户可以预写下一条消息并在提交时进入队列。
        _input.ReadOnly = _interactionSuspended;
        if (busy)
            _placeholderLabel.Visible = false;
        else
            UpdatePlaceholder();
        _input.SetNeedsDraw();
    }

    public void SetInteractionSuspended(bool suspended)
    {
        _interactionSuspended = suspended;
        _input.ReadOnly = _interactionSuspended;
        _input.SetNeedsDraw();
    }

    /// <summary>当前输入框文本。</summary>
    public string CurrentText => _input.Text ?? string.Empty;

    /// <summary>
    /// 设置输入框文本（用于恢复之前的输入）。
    /// </summary>
    public void SetText(string text)
    {
        _suppressCompletion = true;
        _input.Text = text ?? string.Empty;
        _suppressCompletion = false;
        SetNeedsDraw();
    }

    /// <summary>
    /// Sets next-prompt suggestions. The first one is shown as a placeholder
    /// when the input is empty. Tab accepts; Ctrl+Right cycles to the next.
    /// </summary>
    public void SetSuggestions(IReadOnlyList<string> suggestions)
    {
        _suggestions = suggestions ?? [];
        _suggestionIndex = 0;
        UpdatePlaceholder();
    }

    /// <summary>Cycles to the next suggestion (Ctrl+Right).</summary>
    public void CycleSuggestionNext()
    {
        if (_suggestions.Count <= 1) return;
        _suggestionIndex = (_suggestionIndex + 1) % _suggestions.Count;
        UpdatePlaceholder();
    }

    /// <summary>Cycles to the previous suggestion (Ctrl+Left).</summary>
    public void CycleSuggestionPrevious()
    {
        if (_suggestions.Count <= 1) return;
        _suggestionIndex = (_suggestionIndex - 1 + _suggestions.Count) % _suggestions.Count;
        UpdatePlaceholder();
    }

    /// <summary>Accepts the current suggestion into the input (Tab when placeholder visible).</summary>
    public void AcceptSuggestion()
    {
        if (_suggestions.Count == 0 || _suggestionIndex >= _suggestions.Count) return;
        if (!string.IsNullOrEmpty(CurrentText)) return;
        SetInputText(_suggestions[_suggestionIndex]);
        _suggestions = [];
        _placeholderLabel.Visible = false;
        _placeholderLabel.SetNeedsDraw();
    }

    private void UpdatePlaceholder()
    {
        var text = CurrentText;
        if (string.IsNullOrEmpty(text) && _suggestions.Count > 0 && !_isBusy)
        {
            _placeholderLabel.Text = _suggestions[_suggestionIndex];
            _placeholderLabel.Visible = true;
        }
        else
        {
            _placeholderLabel.Visible = false;
        }
        _placeholderLabel.SetNeedsDraw();
    }

    public void ClearInput()
    {
        _pastedText = null;
        _pendingImages.Clear();
        _input.Text = string.Empty;
    }

    /// <summary>
    /// Hides the completion popup if visible. Called by ReplShell.OnKeyDown
    /// as a fallback when ESC doesn't reach ChatInputView.OnInputKeyPress
    /// (e.g., when Editor consumes ESC internally).
    /// </summary>
    public void HideCompletion()
    {
        if (_completion.IsCompletionActive)
            _completion.Hide();
    }

    /// <summary>
    /// Directly sets keyboard focus to the internal Editor.
    /// Use this instead of <c>SetFocus()</c> to ensure the input field
    /// (not just the ChatInputView container) receives keyboard events.
    /// </summary>
    public void FocusInput() => _input.SetFocus();

    /// <summary>
    /// Fires AFTER the Editor text has changed. Used to trigger command/file completion
    /// with the actual current text (unlike KeyDown which fires before text insertion).
    /// </summary>
    private void OnInputTextChanged(object? sender, EventArgs e)
    {
        if (_suppressCompletion) return;

        // Don't clear _pastedText when the text is still our paste summary —
        // the Editor fires deferred text change events after _suppressCompletion
        // is reset, which would prematurely clear the stored original text.
        if (_pastedText is not null && CurrentText.StartsWith("[Pasted text #", StringComparison.Ordinal))
            return;

        // User manually edited the input — discard stored paste content
        _pastedText = null;

        var t = _input.Text ?? string.Empty;
        var firstLine = t.Contains('\n') ? t[..t.IndexOf('\n')] : t;

        // Extract the last word (space-delimited, respecting quotes) to detect
        // slash commands even after other text (e.g. "hello /he" → /help).
        // Without this, typing "/" after any text triggers file-path completion
        // because "/" is treated as a path separator.
        var lastWord = ExtractLastWord(firstLine);
        if (lastWord.StartsWith('/'))
            _completion.UpdateCompletionList(lastWord);
        else
            _completion.TryTypeaheadCompletion(firstLine);

        UpdatePlaceholder();
    }

    /// <summary>
    /// Extracts the last space-delimited word from the input, respecting double-quoted segments.
    /// </summary>
    private static string ExtractLastWord(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var inQuotes = false;
        var lastSpace = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '"') inQuotes = !inQuotes;
            else if (text[i] == ' ' && !inQuotes) lastSpace = i;
        }
        return lastSpace >= 0 ? text[(lastSpace + 1)..] : text;
    }

    private void AddNewline()
    {
        _suppressCompletion = true;
        _input.InsertTextAtCursor("\n");
        _suppressCompletion = false;
        SuperView?.SetNeedsDraw();
    }

    public void SetInputText(string text)
    {
        _pastedText = null;
        _suppressCompletion = true;
        _input.Text = text;
        _input.InsertionPoint = (_input.Text?.Length ?? 0);
        _suppressCompletion = false;
    }

    // 用于 AskUserQuestionTool 的交互式提问

    private bool _isQuestionMode;
    private bool _isLongTextMode;
    private Action<string>? _questionCallback;

    /// <summary>
    /// 进入提问模式 — 显示问题并等待用户输入回答。
    /// </summary>
    public void SetQuestionMode(string question, Action<string> callback, bool longText = false)
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

    /// <summary>
    /// 提交长文本模式的回答（允许空内容）。
    /// </summary>
    private void SubmitLongTextAnswer()
    {
        if (!_isQuestionMode || _questionCallback is null) return;
        _questionCallback(CurrentText.Trim());
    }

    /// <summary>当前是否处于提问模式。</summary>
    public bool IsQuestionMode => _isQuestionMode;

    /// <summary>当前是否处于长文本提问模式。</summary>
    public bool IsLongTextQuestionMode => _isQuestionMode && _isLongTextMode;
}
