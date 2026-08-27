using NSubstitute;
using OneCode.App.Tui;
using OneCode.Core.Keybindings;
using OneCode.Core.Tools;
using Terminal.Gui.App;
using Terminal.Gui.Input;

namespace OneCode.Tests;

/// <summary>
/// Alt+1..4 模式直达（app:mode*）行为守护：
/// - Resolver 层：alt+数字单键精确命中 / 裸数字放行编辑器 / 裸 Tab 无和弦前缀；
/// - 分发层守卫四场景：正常直达 / 补全激活入文 / 选择器挂起交给选择器 /
///   提问模式数字留给答案文本。
/// 回归锚定：裸 Tab 循环切模式保持不变。
/// </summary>
public sealed class ModeDirectKeybindingTests
{
    // —— Resolver 单键匹配 ——

    [Theory]
    [InlineData("1", "app:modeBuild")]
    [InlineData("2", "app:modePlan")]
    [InlineData("3", "app:modeTeam")]
    [InlineData("4", "app:modeGoal")]
    public void Resolve_AltDigit_MatchesModeDirectAction(string digit, string expectedAction)
    {
        var resolver = CreateResolver();

        var result = resolver.Resolve(AltCharKey(digit), ActiveChat);
        result.Result.Should().Be(KeyResolveResult.Match);
        result.Action.Should().Be(expectedAction);
    }


    [Fact]
    public void Resolve_BareTab_NoChordPrefixRegistered_ReturnsNone()
    {
        var resolver = CreateResolver();

        // 模式直达改绑 alt+1..4 后，Chat 上下文中 tab 既无自身绑定也无更长和弦，
        // 不得再开启和弦窗口吞掉后续按键
        resolver.Resolve(Tab(), ActiveChat).Result.Should().Be(KeyResolveResult.None);
    }

    [Fact]
    public void Resolve_BareDigit_NoBinding_FallsThroughToEditor()
    {
        var resolver = CreateResolver();

        resolver.Resolve(CharKey("1"), ActiveChat).Result.Should().Be(
            KeyResolveResult.None,
            "裸数字必须放行给编辑器插入文本");
    }

    // —— 分发守卫四场景 ——

    [Fact]
    public void DispatchInputKey_AltDigit_SetsTargetMode()
    {
        var shell = CreateShell();

        // 裸 Tab 仍立即循环：Build → Plan
        shell.ChatInput.DispatchInputKey(Key.Tab);
        shell.ModeController.Mode.Should().Be(WorkingMode.Plan);

        // 按下 alt+1 → 直达 BUILD（busy 态同样允许）
        shell.ChatInput.SetBusy(true);
        shell.ChatInput.DispatchInputKey(Key.D1.WithAlt);
        shell.ModeController.Mode.Should().Be(WorkingMode.Build);
    }

    [Fact]
    public void DispatchInputKey_CompletionActive_AltDigitIntercepted()
    {
        var shell = CreateShell([new SlashCommandEntry("help", "帮助")]);
        shell.ChatInput.SetInputText("/he");

        // 斜杠前缀下的 Tab 走补全打开（不切模式）；alt+1 由下方守卫拦截
        shell.ChatInput.DispatchInputKey(Key.Tab);
        shell.ModeController.Mode.Should().Be(WorkingMode.Build);
        shell.ChatInput.IsCompletionActive.Should().BeTrue("前置条件：/he 命中 help，补全列表已激活");

        // 补全激活时按数字：守卫拦截模式直达
        shell.ChatInput.DispatchInputKey(Key.D1.WithAlt);
        shell.ModeController.Mode.Should().Be(WorkingMode.Build, "补全激活时数字必须入文，不得直达模式");
        shell.ChatInput.IsCompletionActive.Should().BeTrue();
    }

    [Fact]
    public void DispatchInputKey_SelectorSuspended_DigitGoesToSelector()
    {
        var shell = CreateShell();
        var selector = new InlineSelector("权限", [
            new InlineSelectorOption("allow", "允许"),
            new InlineSelectorOption("deny", "拒绝"),
        ]);
        shell.ShowInlineSelector(selector);

        // 挂起分支先于动作分发返回：Tab 不再循环模式
        shell.ChatInput.DispatchInputKey(Key.Tab);
        shell.ModeController.Mode.Should().Be(WorkingMode.Build);

        shell.ChatInput.DispatchInputKey(Key.D1.WithAlt);
        shell.ModeController.Mode.Should().Be(WorkingMode.Build, "权限选择器期间数字交给选择器");
        selector.SelectedIndex.Should().Be(0);
        selector.ResultTask.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void DispatchInputKey_QuestionMode_DigitReservedForAnswer()
    {
        var shell = CreateShell();
        var wizard = new QuestionWizard("向导", [
            new WizardQuestion("q1", "简答", QuestionType.ShortText),
        ]);
        shell.ShowQuestionWizard(wizard);
        shell.EnterTextModeForCurrentQuestion();

        // 短文本题下 Tab 循环为既有行为；随后的数字必须留给答案文本而非直达
        shell.ChatInput.DispatchInputKey(Key.Tab);
        var modeAfterTab = shell.ModeController.Mode;
        modeAfterTab.Should().NotBe(WorkingMode.Build, "前置：Tab 循环已发生");

        shell.ChatInput.DispatchInputKey(Key.D1.WithAlt);
        shell.ModeController.Mode.Should().Be(modeAfterTab, "提问模式下数字须落入答案文本，守卫必需");
        wizard.ResultTask.IsCompleted.Should().BeFalse();
    }

    // —— 工具 ——

    private static IReadOnlySet<string> ActiveChat =>
        new KeybindingContextManager { FocusContext = KeybindingDefaults.ContextChat }.ActiveContexts;

    private static KeybindingResolver CreateResolver()
    {
        var resolver = new KeybindingResolver();
        resolver.SetBindings([.. KeybindingDefaults.GetDefaultParsedBindings()]);
        return resolver;
    }

    private static IKeyInput Tab()
    {
        var key = Substitute.For<IKeyInput>();
        key.IsTab.Returns(true);
        return key;
    }

    private static IKeyInput CharKey(string ch)
    {
        var key = Substitute.For<IKeyInput>();
        key.Input.Returns(ch);
        return key;
    }

    private static IKeyInput AltCharKey(string ch)
    {
        var key = Substitute.For<IKeyInput>();
        key.Input.Returns(ch);
        key.Meta.Returns(true);
        return key;
    }

    private static ReplShell CreateShell(SlashCommandEntry[]? slashCommands = null)
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
            slashCommands: slashCommands ?? [],
            modeController: new WorkingModeController(),
            keyResolver: resolver,
            keyContextManager: keyContextManager,
            clipboard: null,
            historyProvider: null,
            toolNameProvider: () => []);
    }
}
