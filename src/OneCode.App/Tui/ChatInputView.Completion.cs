using System.Collections.ObjectModel;
using OneCode.Core.Keybindings;

namespace OneCode.App.Tui;

/// <summary>
/// 补全弹窗（斜杠命令 / @ 文件 typeahead）：ChatCompletionController 的
/// 接线与宿主视图侧操作；按键分发在 <see cref="ChatInputView.Keys.cs"/>。
/// </summary>
public sealed partial class ChatInputView
{
    private void WireCompletionStateChanged()
    {
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
    }

    /// <summary>
    /// Replaces the command list used for slash-completion at runtime.
    /// Call after dynamic commands (skills, MCP) are loaded or refreshed.
    /// </summary>
    public void UpdateCommands(IReadOnlyList<SlashCommandEntry> commands)
        => _completion.UpdateCommands(commands);

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

    public void AcceptCompletion()
    {
        var accepted = _completion.Accept();
        if (accepted is not null)
        {
            _suppressCompletion = true;
            _input.Text = accepted;
            _input.InsertionPoint = _input.Text.Length;
            _suppressCompletion = false;
        }
    }
}
