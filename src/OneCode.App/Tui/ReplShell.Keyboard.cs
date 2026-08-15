using OneCode.Core.Keybindings;

namespace OneCode.App.Tui;

/// <summary>
/// Keyboard fallback for <see cref="ReplShell"/> when the chat editor does not
/// have focus. Interaction keys while the input is focused are handled by
/// <see cref="ChatInputView"/> via <see cref="IInteractionSession"/>.
/// </summary>
public sealed partial class ReplShell
{
    // Keyboard: design-spec §5
    protected override bool OnKeyDown(Key kb)
    {
        // 输入框持有焦点时，交互键已由 ChatInputView.OnInputKeyPress 处理。
        // 仅在焦点不在输入框（焦点异常）时兜底，避免同一键被 HandleInteractionKey
        // 处理两次（例如 Down 连跳两格）。
        if (!_chatInput.HasInputFocus && HandleInteractionKey(kb))
            return true;

        // Completion popup ESC handling — the completion popup is NOT managed by
        // OverlayHost (it's added/removed via OnCompletionStateChanged), so it
        // needs its own ESC handler here. This catches ESC even when the Editor
        // consumes it internally (e.g., for canceling selection) before it reaches
        // ChatInputView.OnInputKeyPress.
        if (kb == Key.Esc && _completionVisible)
        {
            _chatInput.HideCompletion();
            FocusChatInput();
            return true;
        }

        // Overlay Esc handling — safety net for when neither the overlay itself
        // nor OverlayHost.OnKeyDown consumed the key (e.g., focus anomaly).
        // Under normal conditions OverlayHost.OnKeyDown handles ESC first.
        if (kb == Key.Esc && _overlayHost.IsOverlayVisible)
        {
            _overlayHost.HandleEsc();
            _overlayHost.Visible = _overlayHost.IsOverlayVisible;
            return true;
        }

        // KeybindingResolver — all configurable shortcuts go through here.
        // ESC for overlays/completion is handled above (not configurable) because
        // dismissal must always work regardless of keybinding overrides.
        var action = TuiKeyAdapter.ResolveAction(kb, _keyResolver, _keyContextManager.ActiveContexts);

        // Ctrl+D — exit the application (when prompt is not focused).
        // When the prompt has focus, ChatInputView.OnInputKeyPress handles it instead.
        if (action == KeybindingDefaults.ActionAppExit && !_chatInput.HasFocus)
        {
            _app.RequestStop();
            return true;
        }

        return base.OnKeyDown(kb);
    }
}
