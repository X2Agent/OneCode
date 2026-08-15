namespace OneCode.App.Tui;

using Terminal.Gui.Drivers;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;

/// <summary>
/// Multi-line text input built on Terminal.Gui.Editor's <see cref="Editor"/>.
/// Supports up to <see cref="MaxVisibleLines"/> visible rows; scrolls beyond that.
/// Exposes a linear InsertionPoint (character offset into the full text).
/// </summary>
internal sealed class ChatTextEditor : View
{
    public const int MaxVisibleLines = 4;

    private readonly Editor _editor;
    private int _currentHeight = 1;
    private bool _suppressEvents;
    private bool _insertingText;
    private int _previousLineCount = 1;
    private int _previousTextLength;
    // Set to true when LargeTextPasted fires, to prevent the Editor from
    // inserting more raw text before HandlePastedText (deferred via
    // _app.Invoke) replaces the content with the collapsed summary.
    // Restored by ResumeAfterPaste() after the summary is set.
    private bool _pasteSuppressed;
    // KeyDownEvent may set e.Handled, but Terminal.Gui can still propagate the
    // key to this wrapper (value-type Key). Remember consumption so OnKeyDown
    // does not bubble into ReplShell and double-dispatch interaction keys.
    private bool _keyDownConsumedByOwner;

    public event EventHandler<Key>? KeyDownEvent;
    public event EventHandler? ContentsChanged;

    /// <summary>
    /// When enabled, Alt+Left/Alt+Right bypass Editor cursor handling and are
    /// forwarded to the active question wizard for cross-question navigation.
    /// </summary>
    public bool QuestionNavigationEnabled { get; set; }

    /// <summary>
    /// Raised when a large text paste is detected — i.e. the line count jumps
    /// by more than 1 in a single change and exceeds <see cref="MaxVisibleLines"/>.
    /// Subscribers should collapse the text into a one-line summary.
    /// </summary>
    public event Action<string>? LargeTextPasted;

    public ChatTextEditor()
    {
        // This wrapper participates in the focus chain only so Terminal.Gui can
        // focus its Editor descendant. It must not be a separate tab stop.
        CanFocus = true;
        TabStop = TabBehavior.NoStop;
        Width = Dim.Fill();
        Height = Dim.Fill();

        _editor = new Editor
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            WordWrap = false,
            Multiline = true,
            CanFocus = true,
            TabStop = TabBehavior.TabStop,
        };

        // Shift+Enter and Ctrl+V must NOT be bound to Editor commands — both
        // need to fall through to KeyDown, where ChatInputView.OnInputKeyPress
        // intercepts them via KeybindingResolver (supports user remapping of
        // chat:newline and chat:paste).
        //
        // Editor ships with DEFAULT KeyBindings for Ctrl+V (Command.Paste) and
        // possibly Shift+Enter. KeyBindings are processed BEFORE KeyDown events,
        // so any matching binding suppresses KeyDown entirely — ChatInputView would
        // never see the key. We must explicitly Remove both defaults.
        // (Adding new bindings is unnecessary and harmful: it re-creates the
        // same suppression problem we're trying to avoid.)
        _editor.KeyBindings.Remove(Key.V.WithCtrl);
        _editor.KeyBindings.Remove(Key.Enter.WithShift);
        _editor.KeyBindings.Remove(Key.Enter);
        _editor.KeyBindings.Remove(Key.Esc);
        // Remove Editor's default bindings for scroll keys so they propagate
        // to KeyDown → KeyDownEvent → ChatInputView → KeybindingResolver.
        _editor.KeyBindings.Remove(Key.PageUp);
        _editor.KeyBindings.Remove(Key.PageDown);
        _editor.KeyBindings.Remove(Key.PageUp.WithCtrl);
        _editor.KeyBindings.Remove(Key.PageDown.WithCtrl);
        _editor.KeyBindings.Remove(Key.CursorUp.WithShift);
        _editor.KeyBindings.Remove(Key.CursorDown.WithShift);

        _editor.KeyDown += (_, e) =>
        {
            _keyDownConsumedByOwner = false;

            // Intercept scroll keys and question navigation BEFORE Editor processes
            // them — Editor's internal bindings would otherwise consume them.
            if (!_suppressEvents && IsQuestionNavigationKey(e))
            {
                KeyDownEvent?.Invoke(this, e);
                e.Handled = true;
            }
            else if (!_suppressEvents && IsScrollKey(e))
            {
                // Intercept scroll keys (PageUp/PageDown/Ctrl+PgUp/Ctrl+PgDn) BEFORE
                // Editor processes them — Editor's internal OnKeyDown may consume
                // them for cursor movement, preventing KeyDownEvent from firing.
                KeyDownEvent?.Invoke(this, e);
                e.Handled = true;
            }
            else if (e.NoShift.NoCtrl.NoAlt == Key.Esc && !_suppressEvents)
            {
                // Intercept Esc BEFORE anything else — Editor's internal OnKeyDown
                // may consume it (cancel-selection) and prevent KeyDownEvent from
                // reaching ChatInputView. Strip modifiers: terminals often report Esc
                // with Meta/Alt set, so `e == Key.Esc` alone misses those events.
                KeyDownEvent?.Invoke(this, e);
                e.Handled = true;
            }
            else if (!_suppressEvents &&
                !_editor.ReadOnly &&
                !e.IsShift &&
                ShouldHandleAsShiftEnter(e))
            {
                // ConPTY fallback: without kitty keyboard protocol, Shift+Enter is
                // encoded by the terminal as ESC+\r, which Terminal.Gui decodes as
                // Ctrl+Alt+M (ESC = Alt, \r = Ctrl+M = 0x0D). This encoding is
                // unambiguous, so we treat it as deterministic Shift+Enter.
                // For bare Key.Enter (some terminals strip the ESC prefix), fall
                // back to GetAsyncKeyState to distinguish from plain Enter.
                InsertTextAtCursor("\n");
                e.Handled = true;
            }
            else if (!_suppressEvents)
            {
                KeyDownEvent?.Invoke(this, e);
            }

            if (e.Handled)
                _keyDownConsumedByOwner = true;
        };
        _editor.Document.TextChanged += (_, _) => OnDocumentChanged();
        // Conventional editor caret: a single blinking vertical bar (DECSCUSR Ps=5),
        // universally supported by modern terminals incl. Windows Terminal/ConPTY.
        // Setting a "steady" caret (e.g. SteadyBar, Ps=6) is NOT honored by many
        // terminals, which fall back to their default (block) shape and then blink at
        // an erratic cadence on every Terminal.Gui redraw — the "weird" flicker users
        // saw. BlinkingBar keeps the caret visible at a stable, standard rate.
        _editor.Cursor = _editor.Cursor with { Style = CursorStyle.BlinkingBar };

        Add(_editor);
    }

    private void OnDocumentChanged()
    {
        var currentLineCount = LineCount;
        var currentTextLength = _editor.Document.TextLength;

        // Large paste detection: the text changed by more than a single
        // character in one event (manual typing adds exactly 1 char at a time).
        // We check BOTH line-count jump and text-length jump because ConPTY
        // may deliver pasted text line-by-line (each line is a multi-char chunk
        // that increases line count by only 1, but text length by many).
        var lineJump = currentLineCount - _previousLineCount;
        var lengthJump = currentTextLength - _previousTextLength;
        if (!_suppressEvents && !_insertingText &&
            currentLineCount > MaxVisibleLines &&
            (lineJump > 1 || lengthJump > 1) &&
            LargeTextPasted is not null)
        {
            // Prevent the Editor from inserting more raw text before the
            // deferred HandlePastedText replaces the content with the summary.
            _pasteSuppressed = true;
            _editor.ReadOnly = true;
            LargeTextPasted.Invoke(Text);
            // HandlePastedText (in ChatInputView, deferred via _app.Invoke) will
            // reset _editor.Text to the collapsed summary and call
            // ResumeAfterPaste() to restore the read-only state.
            // Skip the normal ContentsChanged dispatch to avoid
            // OnInputTextChanged clearing the _pastedText state.
            return;
        }

        _previousLineCount = currentLineCount;
        _previousTextLength = currentTextLength;
        AdjustHeight();
        if (!_suppressEvents && !_insertingText)
            ContentsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Restores the Editor's read-only state after a paste-collapse operation.
    /// Called by ChatInputView.HandlePastedText after setting the collapsed summary.
    /// </summary>
    public void ResumeAfterPaste()
    {
        if (_pasteSuppressed)
        {
            _pasteSuppressed = false;
            _editor.ReadOnly = false;
        }
    }

    public new string Text
    {
        get => _editor.Text ?? string.Empty;
        set
        {
            _suppressEvents = true;
            try
            {
                _editor.Text = value ?? string.Empty;
                // Keep _previousLineCount/Length in sync so the next TextChanged
                // doesn't mistake the programmatic reset for a paste.
                _previousLineCount = LineCount;
                _previousTextLength = _editor.Document.TextLength;
                AdjustHeight();
            }
            finally
            {
                _suppressEvents = false;
            }
        }
    }

    public int InsertionPoint
    {
        get => _editor.CaretOffset;
        set => _editor.CaretOffset = Math.Clamp(value, 0, _editor.Document.TextLength);
    }

    /// <summary>Insert text at current cursor position without resetting whole text.</summary>
    public void InsertTextAtCursor(string s)
    {
        _insertingText = true;
        try
        {
            _editor.Document.Insert(_editor.CaretOffset, s);
            _editor.CaretOffset += s.Length;
        }
        finally
        {
            _insertingText = false;
        }
        // Document.Insert synchronously fires TextChanged → OnDocumentChanged,
        // which already updates _previousLineCount and calls AdjustHeight().
        // Only ContentsChanged dispatch is skipped (because _insertingText was
        // true during the nested event), so we manually fire it here.
        ContentsChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool ReadOnly
    {
        get => _editor.ReadOnly;
        set => _editor.ReadOnly = value;
    }

    public new void SetFocus() => _editor.SetFocus();

    /// <summary>True when the inner Editor currently has keyboard focus.</summary>
    internal bool HasEditorFocus => _editor.HasFocus;

    public new void SetNeedsDraw()
    {
        _editor.SetNeedsDraw();
        base.SetNeedsDraw();
    }

    // Editor uses 1-based line/column; callers expect 0-based.
    public int CurrentRow => _editor.Document.GetLocation(_editor.CaretOffset).Line - 1;
    public int CurrentColumn => _editor.Document.GetLocation(_editor.CaretOffset).Column - 1;
    public int LineCount => _editor.Document.LineCount;

    // Safety net: if e.Handled = true in the KeyDown event handler doesn't
    // propagate (Key may be a struct), the Editor's OnKeyDown returns false
    // and the key propagates to this parent view. Intercept it here to
    // prevent further processing (e.g., ReplShell.OnKeyDown re-resolving
    // the key and triggering an unwanted action).
    protected override bool OnKeyDown(Key e)
    {
        if (_keyDownConsumedByOwner)
        {
            _keyDownConsumedByOwner = false;
            return true;
        }

        if (!_editor.ReadOnly &&
            !e.IsShift &&
            ShouldHandleAsShiftEnter(e))
        {
            return true;
        }
        return base.OnKeyDown(e);
    }

    /// <summary>
    /// Checks if the key is a scroll-related key that should bypass Editor
    /// and be forwarded to ChatInputView for KeybindingResolver processing.
    /// </summary>
    private bool IsQuestionNavigationKey(Key e)
    {
        if (!QuestionNavigationEnabled || !e.IsAlt || e.IsCtrl || e.IsShift)
            return false;
        var bare = e.NoShift.NoCtrl.NoAlt;
        return bare == Key.CursorLeft || bare == Key.CursorRight;
    }

    private static bool IsScrollKey(Key e)
    {
        var bare = e.NoShift.NoCtrl.NoAlt;
        return bare == Key.PageUp
            || bare == Key.PageDown
            || (e.IsCtrl && (bare == Key.PageUp || bare == Key.PageDown))
            || (e.IsShift && (bare == Key.CursorUp || bare == Key.CursorDown));
    }

    /// <summary>
    /// Determines whether the key should be handled as Shift+Enter (newline).
    ///
    /// ConPTY encodes Shift+Enter as ESC+\r, which Terminal.Gui decodes as
    /// Ctrl+Alt+M. This is unambiguous (users don't intentionally press
    /// Ctrl+Alt+M), so it's treated as deterministic Shift+Enter without
    /// physical key state check — this avoids race conditions when typing fast.
    ///
    /// For bare Key.Enter (some terminals strip the ESC prefix), falls back
    /// to GetAsyncKeyState to distinguish from plain Enter.
    /// </summary>
    private static bool ShouldHandleAsShiftEnter(Key e)
    {
        // Ctrl+Alt+M (ESC+\r) — deterministic Shift+Enter, no physical check needed
        if (e == Key.M.WithCtrl.WithAlt) return true;
        // Bare Enter — may be Shift+Enter on some terminals; check physical state
        if (e.NoShift.NoCtrl.NoAlt == Key.Enter) return KeyboardState.IsShiftPressed();
        return false;
    }

    private void AdjustHeight()
    {
        var lines = Math.Clamp(LineCount, 1, MaxVisibleLines);
        if (_currentHeight != lines)
        {
            _currentHeight = lines;
            SuperView?.SetNeedsDraw();
            SetNeedsDraw();
        }
    }
}
