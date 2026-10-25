using NSubstitute;
using OneCode.App.Commands;
using OneCode.Core.Keybindings;
using OneCode.Core.Tools;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using OneCode.Tui;

namespace OneCode.Tests;

/// <summary>
/// 工作模式切换（裸 Tab 循环）行为守护：
/// - Resolver 层：裸数字放行编辑器 / 裸 Tab 无自身绑定、无和弦前缀；
/// - 分发层：Tab 三档循环；补全激活 / 选择器挂起 / 提问模式下 Tab 不越权。
/// 模式切换唯一入口为裸 Tab 循环，未设直达单键。
/// </summary>
public sealed class ModeSwitchKeybindingTests
{
    // —— Resolver 单键匹配 ——

    [Fact]
    public void Resolve_BareTab_NoChordPrefixRegistered_ReturnsNone()
    {
        var resolver = CreateResolver();

        // Chat 上下文中 tab 既无自身绑定也无更长和弦，
        // 不得开启和弦窗口吞掉后续按键
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

    // —— 分发守卫三场景 ——

    [Fact]
    public void DispatchInputKey_Tab_CyclesAllThreeModes()
    {
        var shell = CreateShell();

        // 裸 Tab 循环：Build → Plan → Goal → Build（空闲态）
        shell.ChatInput.DispatchInputKey(Key.Tab);
        shell.ModeController.Mode.Should().Be(WorkingMode.Plan);
        shell.ChatInput.DispatchInputKey(Key.Tab);
        shell.ModeController.Mode.Should().Be(WorkingMode.Goal);
        shell.ChatInput.DispatchInputKey(Key.Tab);
        shell.ModeController.Mode.Should().Be(WorkingMode.Build);
    }

    [Fact]
    public void DispatchInputKey_RunActive_TabDoesNotCycleModeAndResumesAfterRun()
    {
        var shell = CreateShell();

        // 运行期间 Tab 被吞掉：模式切换会让工作流与活跃 run 脱节
        shell.ChatInput.SetBusy(true);
        shell.ChatInput.DispatchInputKey(Key.Tab);
        shell.ModeController.Mode.Should().Be(WorkingMode.Build, "run 活跃期间不得切换工作模式");

        // run 结束后循环恢复
        shell.ChatInput.SetBusy(false);
        shell.ChatInput.DispatchInputKey(Key.Tab);
        shell.ModeController.Mode.Should().Be(WorkingMode.Plan);
    }

    [Fact]
    public void DispatchInputKey_CompletionActive_TabDoesNotCycleMode()
    {
        var shell = CreateShell([new SlashCommandEntry("help", "帮助")]);
        shell.ChatInput.SetInputText("/he");

        // 斜杠前缀下的 Tab 走补全打开，不切模式
        shell.ChatInput.DispatchInputKey(Key.Tab);
        shell.ModeController.Mode.Should().Be(WorkingMode.Build);
        shell.ChatInput.IsCompletionActive.Should().BeTrue("前置条件：/he 命中 help，补全列表已激活");
    }

    [Fact]
    public void DispatchInputKey_SelectorSuspended_TabGoesToSelector()
    {
        var shell = CreateShell();
        var selector = new InlineSelector("权限", [
            new InlineSelectorOption("allow", "允许"),
            new InlineSelectorOption("deny", "拒绝"),
        ]);
        shell.ShowInlineSelector(selector);

        // 挂起分支先于动作分发返回：Tab 不循环模式，按键交给选择器
        shell.ChatInput.DispatchInputKey(Key.Tab);
        shell.ModeController.Mode.Should().Be(WorkingMode.Build);
        selector.SelectedIndex.Should().Be(0);
        selector.ResultTask.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void DispatchInputKey_QuestionMode_TabDoesNotAnswerWizard()
    {
        var shell = CreateShell();
        var wizard = new QuestionWizard("向导", [
            new WizardQuestion("q1", "简答", QuestionType.ShortText),
        ]);
        shell.ShowQuestionWizard(wizard);
        shell.EnterTextModeForCurrentQuestion();

        // 短文本题下 Tab 循环为既有行为；Tab 不得顺带完成向导作答
        shell.ChatInput.DispatchInputKey(Key.Tab);
        shell.ModeController.Mode.Should().NotBe(WorkingMode.Build, "前置：Tab 循环已发生");
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
            new ReplShellEnvironment(app, "test-model"),
            new WorkingModeController(),
            new ReplShellKeyServices(resolver, keyContextManager),
            new ReplShellInputSources(slashCommands ?? []));
    }
}
