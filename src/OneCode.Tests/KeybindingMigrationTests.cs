using NSubstitute;
using OneCode.App.Tui;
using OneCode.Core.Keybindings;
using Terminal.Gui.App;
using Terminal.Gui.Input;

namespace OneCode.Tests;

/// <summary>
/// 硬编码迁移 Resolver 后的行为守护（keyboard-first Phase 2）：
/// - eager-fire：tab 自身绑定 autocomplete:accept 时立即触发；
///   模式直达已改绑 alt+1..4，tab 不再是和弦前缀；纯前缀和弦（ctrl+x）不受影响；
/// - DiffView / InlineSelector 经 ContextDiff/ContextSelector 分发，
///   默认行为与迁移前一致，用户重映射可生效。
/// </summary>
public sealed class KeybindingMigrationTests
{
    // —— eager-fire ——

    [Fact]
    public void Resolve_TabWithCompletionActive_FiresAcceptWithoutChordPending()
    {
        var resolver = CreateResolver();
        var contexts = Contexts(Chat, Autocomplete);

        // tab 自身有绑定 → 立即 Match（而非 ChordStarted）
        var tabResult = resolver.Resolve(new TuiKeyAdapter(Key.Tab), contexts);
        tabResult.Result.Should().Be(KeyResolveResult.Match);
        tabResult.Action.Should().Be(KeybindingDefaults.ActionAutocompleteAccept);

        // 模式直达已改绑 alt+1..4：随后的裸数字不再是和弦尾键
        var digit = resolver.Resolve(CharKey("1"), contexts);
        digit.Result.Should().Be(KeyResolveResult.None);
    }

    [Fact]
    public void Resolve_PurePrefixWithoutSelfBinding_StillWaitsForTail()
    {
        // ctrl+x ctrl+k 为用户自定义和弦示例：ctrl+x 自身无绑定，必须保持等待
        var resolver = CreateResolver();
        resolver.SetBindings([.. KeybindingDefaults.GetDefaultParsedBindings(),
            new KeybindingEntry(
                KeybindingDefaults.ContextChat,
                KeybindingParser.ParseChord("ctrl+x ctrl+k"),
                KeybindingDefaults.ActionChatKillAgents)]);
        var contexts = Contexts(Chat);

        var head = resolver.Resolve(CtrlKey("x"), contexts);
        head.Result.Should().Be(KeyResolveResult.ChordStarted,
            "无自身绑定的前缀不得被 eager-fire 改变语义");

        var tail = resolver.Resolve(CtrlKey("k"), contexts);
        tail.Action.Should().Be(KeybindingDefaults.ActionChatKillAgents);
    }

    // —— ChatInputView：补全激活时 Tab 走 accept 且不切模式 ——

    [Fact]
    public void DispatchInputKey_CompletionTab_AcceptsViaResolverWithoutModeChange()
    {
        var shell = CreateShell();
        shell.ChatInput.SetInputText("/he");
        shell.ChatInput.DispatchInputKey(Key.Tab); // 打开补全列表
        shell.ChatInput.IsCompletionActive.Should().BeTrue();

        // 第二次 Tab：eager-fire 命中 autocomplete:accept，循环补全选择
        var before = shell.ChatInput.SelectedSuggestionIndex;
        shell.ChatInput.DispatchInputKey(Key.Tab);
        shell.ChatInput.IsCompletionActive.Should().BeTrue();
        shell.ModeController.Mode.Should().Be(WorkingMode.Build, "补全期间 Tab 不得切模式");
        shell.ChatInput.SelectedSuggestionIndex.Should().BeGreaterThan(before - 1, "循环仍发生");
    }

    // —— InlineSelector：selector:* 迁移 ——

    [Fact]
    public void InlineSelector_DefaultBindings_KeepArrowEnterEscBehavior()
    {
        var selector = new InlineSelector("权限", [
            new InlineSelectorOption("allow", "允许"),
            new InlineSelectorOption("deny", "拒绝"),
        ]);

        selector.HandleKey(Terminal.Gui.Input.Key.CursorDown).Should().BeTrue();
        selector.SelectedIndex.Should().Be(1);

        selector.HandleKey(Terminal.Gui.Input.Key.Esc).Should().BeTrue();
        selector.ResultTask.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task InlineSelector_UserRemap_LetterNavigationTakesEffect()
    {
        // 模拟宿主注入全局键位系统 + 用户把 selector:next 重映射到字母 n。
        var resolver = CreateResolver();
        resolver.SetBindings([.. KeybindingDefaults.GetDefaultParsedBindings(),
            new KeybindingEntry(
                KeybindingDefaults.ContextSelector,
                KeybindingParser.ParseChord("n"),
                KeybindingDefaults.ActionSelectorNext)]);
        var manager = new KeybindingContextManager { FocusContext = KeybindingDefaults.ContextChat };

        var selector = new InlineSelector("权限", [
            new InlineSelectorOption("a", "甲"),
            new InlineSelectorOption("b", "乙"),
        ]);
        selector.AttachKeybindingSystem(resolver, manager);

        selector.HandleKey(Terminal.Gui.Input.Key.N).Should().BeTrue();
        selector.SelectedIndex.Should().Be(1);

        selector.HandleKey(Terminal.Gui.Input.Key.Enter).Should().BeTrue();
        var result = await selector.ResultTask;
        result.SelectedId.Should().Be("b");
    }

    // —— DiffView：diff:* 迁移 ——

    [Fact]
    public void DiffView_DefaultBindings_ScrollArrowsVimKeys()
    {
        var view = new DiffView(CreateResolver());
        view.SetDiff(string.Join('\n', Enumerable.Range(1, 50).Select(i => $"line {i}")));

        view.DispatchKey(Terminal.Gui.Input.Key.J).Should().BeTrue();
        view.ScrollOffset.Should().Be(1);

        view.DispatchKey(Terminal.Gui.Input.Key.PageDown).Should().BeTrue();
        view.ScrollOffset.Should().BeGreaterThan(1);

        view.DispatchKey(Terminal.Gui.Input.Key.End).Should().BeTrue();
        view.DispatchKey(Terminal.Gui.Input.Key.Home).Should().BeTrue();
        view.ScrollOffset.Should().Be(0);
    }

    [Fact]
    public void DiffView_UnremappedKey_IsNotHandled()
    {
        // 迁移后未绑定的按键不再被吞掉（如字母 q），交回 overlay 层处理。
        var view = new DiffView(CreateResolver());
        view.SetDiff("+a\n-b\n c");

        view.DispatchKey(Terminal.Gui.Input.Key.Q).Should().BeFalse();
    }

    // —— 工具 ——

    private const string Chat = KeybindingDefaults.ContextChat;
    private const string Autocomplete = KeybindingDefaults.ContextAutocomplete;

    private static IReadOnlySet<string> Contexts(params string[] names) =>
        new HashSet<string>(names.Append(KeybindingDefaults.ContextGlobal));

    private static KeybindingResolver CreateResolver()
    {
        var resolver = new KeybindingResolver();
        resolver.SetBindings([.. KeybindingDefaults.GetDefaultParsedBindings()]);
        return resolver;
    }

    private static IKeyInput CharKey(string ch)
    {
        var key = Substitute.For<IKeyInput>();
        key.Input.Returns(ch);
        return key;
    }

    private static IKeyInput CtrlKey(string ch)
    {
        var key = Substitute.For<IKeyInput>();
        key.Input.Returns(ch);
        key.Ctrl.Returns(true);
        return key;
    }

    private static ReplShell CreateShell()
    {
        var app = Substitute.For<IApplication>();
        app.Invoke(Arg.Do<Action>(action => action()));
        app.AddTimeout(Arg.Any<TimeSpan>(), Arg.Any<Func<bool>>()).Returns(true);

        var resolver = CreateResolver();
        var keyContextManager = new KeybindingContextManager
        {
            FocusContext = KeybindingDefaults.ContextChat,
        };

        return new ReplShell(
            app,
            version: "test",
            model: "test-model",
            sshHost: null,
            slashCommands: [new SlashCommandEntry("help", "帮助")],
            modeController: new WorkingModeController(),
            keyResolver: resolver,
            keyContextManager: keyContextManager,
            clipboard: null,
            historyProvider: null,
            toolNameProvider: () => []);
    }
}
