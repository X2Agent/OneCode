using OneCode.Infrastructure.Media;
using OneCode.Core.Keybindings;
using System.Collections.ObjectModel;

namespace OneCode.App.Tui;

/// <summary>
/// Complete chat input region: separator, prompt state, multiline editor,
/// completion popup coordination, paste handling, and dynamic bottom layout.
/// Key dispatch in ChatInputView.Keys.cs, paste in ChatInputView.Paste.cs,
/// question mode in ChatInputView.Question.cs, completion wiring in
/// ChatInputView.Completion.cs, view construction and draw geometry in
/// ChatInputView.Layout.cs.
/// </summary>
public sealed partial class ChatInputView : View
{
    public const int MinVisibleLines = 1;
    public const int MinHeight = 1 + MinVisibleLines; // 2
    public const int MaxHeight = 1 + ChatTextEditor.MaxVisibleLines; // 6

    /// <summary>
    /// 输入框总高度变化通知（当物理行数增减导致总高度在 2~6 行间变化时触发）。
    /// 参数为新的总行数（1 行分隔线 + N 行编辑器文本）。
    /// </summary>
    public event Action<int>? InputHeightChanged;

    private readonly IApplication _app;
    private readonly WorkingModeController _modeController;
    private readonly IReadOnlyList<SlashCommandEntry> _allCommands;

    // 子控件在 BuildViews()（构造函数调用）中创建，见 ChatInputView.Layout.cs
    private Label _separatorLabel = null!;
    private ChatTextEditor _input = null!;
    private int _lastHeight = MinHeight;
    private int _lastBottomOffset;
    private ListView _completionList = null!;
    private FrameView _completionFrame = null!;

    private bool _isBusy;
    private bool _interactionSuspended;
    // Set when OnInputKeyPress consumes an interaction key so a bubbled
    // OnKeyDown (Terminal.Gui may drop Key.Handled) does not re-dispatch.
    private bool _suppressKeyBubble;

    private ObservableCollection<string> _completionItems = [];

    // Attachment management state
    // Pasted large text is collapsed into [Pasted text #N +L lines] tokens,
    // and pasted images are saved to temp files with [Image #N] tags.
    // Both are managed by _attachmentRegistry with atomic lifecycles.
    private readonly PendingAttachmentRegistry _attachmentRegistry = new();

    private Label _placeholderLabel = null!;
    private IReadOnlyList<string> _suggestions = [];
    private int _suggestionIndex;

    private readonly ChatCompletionController _completion;
    private readonly ChatHistoryController _historyCtrl;
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
    /// <summary>Raised when the user presses Tab in a non-completion context to cycle the working mode.</summary>
    public event Action? CycleModeRequested;

    /// <summary>加宽右侧侧边栏（Ctrl+Shift+→，app:sidebarWider）。由 ReplShell 接线到宽度调整。</summary>
    public event Action? SidebarWiderRequested;

    /// <summary>收窄右侧侧边栏（Ctrl+Shift+←，app:sidebarNarrower）。由 ReplShell 接线到宽度调整。</summary>
    public event Action? SidebarNarrowerRequested;

    /// <summary>切换右侧侧边栏可见性（Ctrl+G，app:sidebarToggle）。由 ReplShell 接线。</summary>
    public event Action? SidebarToggleRequested;

    /// <summary>
    /// 直达指定工作模式（Alt+1..4，app:mode* 动作）。
    /// 视图内部接线到 <see cref="WorkingModeController.Mode"/>，复用 ModeChanged
    /// 徽章闪烁（AgentStatusBar 订阅），无需跨层订阅。
    /// </summary>
    internal event Action<WorkingMode>? ModeDirectRequested;

    /// <summary>
    /// Raised when an image paste is rejected because the current model doesn't
    /// support multimodal input. The caller should show a visible error message.
    /// </summary>
    public event Action? ImagePasteRejected;

    /// <summary>
    /// 在 TEAM 模式下循环切换已注册团队（Shift+Tab）。
    /// 调用方（OneCodeToplevel）将其桥接到 TuiContext.CycleTeam 回调，
    /// 并刷新 AgentStatusBar 显示的团队名。
    /// </summary>
    public event Action? CycleTeamRequested;

    /// <summary>
    /// 活跃交互会话（提问向导 / 内联选择器），由 ReplShell 注入。
    /// 交互接管期间的按键交由会话统一处理（见 <see cref="IInteractionSession"/>）。
    /// </summary>
    internal IInteractionSession? InteractionSession { get; set; }

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

        // Terminal.Gui v2 enables bracketed paste mode automatically.
        // Pasted text is delivered via IApplication.Paste event, NOT via
        // KeyDown key events. Subscribe here to intercept bracketed-paste
        // deliveries and route them through HandlePastedText (which applies
        // multi-line folding, image detection, and path expansion).
        // NOTE: Ctrl+V initiated paste is handled separately in OnInputKeyPress
        // via KeybindingResolver — this subscription only covers the
        // bracketed-paste channel (e.g., terminal menu paste).
        _app.Paste += OnApplicationPaste;

        WireCompletionStateChanged();

        BuildViews();
    }

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
        // 程序化整体替换不触发 ContentsChanged，需显式对账附件生命周期。
        _attachmentRegistry.PruneMissing(_input.Text ?? string.Empty);
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
        _attachmentRegistry.Clear();
        _input.Text = string.Empty;
    }

    /// <summary>
    /// Directly sets keyboard focus to the internal Editor.
    /// Use this instead of <c>SetFocus()</c> to ensure the input field
    /// (not just the ChatInputView container) receives keyboard events.
    /// </summary>
    public void FocusInput() => _input.SetFocus();

    /// <summary>
    /// True when the nested editor currently has keyboard focus.
    /// ReplShell uses this to skip interaction-key fallback and avoid double-handling.
    /// </summary>
    internal bool HasInputFocus => _input.HasEditorFocus;

    /// <summary>
    /// Fires AFTER the Editor text has changed. Used to trigger command/file completion
    /// with the actual current text (unlike KeyDown which fires before text insertion).
    /// </summary>
    private void OnInputTextChanged(object? sender, EventArgs e)
    {
        // 生命周期对账：剔除占位符已随文本编辑消失的附件（含 Editor 内部撤销）。
        _attachmentRegistry.PruneMissing(_input.Text ?? string.Empty);

        if (_suppressCompletion) return;

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
        _suppressCompletion = true;
        _input.Text = text;
        _input.InsertionPoint = (_input.Text?.Length ?? 0);
        _suppressCompletion = false;
        // 程序化整体替换不触发 ContentsChanged，需显式对账附件生命周期。
        _attachmentRegistry.PruneMissing(_input.Text ?? string.Empty);
    }

    /// <summary>
    /// Expands all Unicode PUA tokenized paste attachments back into their original text.
    /// Preserves all user-typed text before, between, and after folded attachments.
    /// </summary>
    public string ExpandAttachments(string text) => _attachmentRegistry.ExpandAttachments(text);
}
