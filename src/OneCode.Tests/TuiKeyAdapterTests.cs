using OneCode.App.Tui;
using OneCode.Core.Keybindings;
using Terminal.Gui.Input;

namespace OneCode.Tests;

public sealed class TuiKeyAdapterTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResolveAction_ShiftArrow_MapsToConversationScroll(bool scrollUp)
    {
        var resolver = new KeybindingResolver();
        resolver.SetBindings([.. KeybindingDefaults.GetDefaultParsedBindings()]);
        var contexts = new HashSet<string>
        {
            KeybindingDefaults.ContextGlobal,
            KeybindingDefaults.ContextChat,
        };
        var key = scrollUp ? Key.CursorUp.WithShift : Key.CursorDown.WithShift;
        var adapter = new TuiKeyAdapter(key);

        var action = adapter.ResolveAction(resolver, contexts);

        action.Should().Be(scrollUp
            ? KeybindingDefaults.ActionChatScrollUp
            : KeybindingDefaults.ActionChatScrollDown);
    }

    // 回归锚定：TGv2 的 Key.AsRune 带 Ctrl/Alt 时返回 default，若适配层不剥离
    // 修饰键，ctrl+v（粘贴）/ ctrl+t（导航）等字母类组合键
    // 永远无法经 Resolver 匹配。
    [Theory]
    [InlineData("v", "chat:paste")]
    [InlineData("t", "chat:enterTranscript")]
    public void ResolveAction_CtrlLetter_MatchesBinding(string letter, string expectedAction)
    {
        var resolver = new KeybindingResolver();
        resolver.SetBindings([.. KeybindingDefaults.GetDefaultParsedBindings()]);
        var contexts = new HashSet<string> { KeybindingDefaults.ContextChat };

        var key = letter switch
        {
            "g" => Key.G.WithCtrl,
            "v" => Key.V.WithCtrl,
            _ => Key.T.WithCtrl,
        };
        var adapter = new TuiKeyAdapter(key);

        adapter.ResolveAction(resolver, contexts).Should().Be(expectedAction);
    }
}
