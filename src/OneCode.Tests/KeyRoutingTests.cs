using NSubstitute;
using OneCode.App.Tui;
using OneCode.Core.Keybindings;
using OneCode.Core.Tools;
using Terminal.Gui.App;

namespace OneCode.Tests;

/// <summary>
/// 交互会话键路由行为断言（tui-refactor B1/B2）：
/// 挂起态（选择题/内联选择器）与提问态（文本题）的按键统一经
/// ReplShell.HandleInteractionKey 处理，交互行由尾部交互区域整体替换。
/// </summary>
public sealed class KeyRoutingTests
{
    [Fact]
    public void InlineSelector_Keys_ConsumeAndUpdateTailRegion()
    {
        var shell = CreateShell();
        var selector = new InlineSelector("权限", [
            new InlineSelectorOption("allow", "允许"),
            new InlineSelectorOption("deny", "拒绝"),
        ]);

        shell.ShowInlineSelector(selector);
        var linesWhenShown = shell.Transcript.MessageView.TotalLines;
        linesWhenShown.Should().BeGreaterThan(0);

        shell.HandleInteractionKey(Terminal.Gui.Input.Key.CursorDown).Should().BeTrue();
        selector.SelectedIndex.Should().Be(1);

        // 尾部区域整体替换 — 总行数不随选择变化累积
        shell.Transcript.MessageView.TotalLines.Should().Be(linesWhenShown);

        shell.HandleInteractionKey(Terminal.Gui.Input.Key.Esc).Should().BeTrue();
        selector.ResultTask.IsCompleted.Should().BeTrue();

        shell.DismissInlineSelector();
        shell.Transcript.MessageView.TotalLines.Should().Be(0);
    }

    [Fact]
    public void ChoiceQuestion_UnconsumedKey_DoesNotFallThroughToSelector()
    {
        var shell = CreateShell();
        var wizard = new QuestionWizard("向导", [
            new WizardQuestion("q1", "选择", QuestionType.SingleChoice, ["甲", "乙"]),
        ]);
        shell.ShowQuestionWizard(wizard);

        // 普通字符键不被向导消耗
        shell.HandleInteractionKey(Terminal.Gui.Input.Key.A).Should().BeFalse();
        // 向导仍在当前题
        wizard.CurrentIndex.Should().Be(0);
    }

    [Fact]
    public void ChoiceToTextTransition_EnterEntersQuestionMode()
    {
        var shell = CreateShell();
        var wizard = new QuestionWizard("向导", [
            new WizardQuestion("q1", "选择", QuestionType.SingleChoice, ["甲", "乙"]),
            new WizardQuestion("q2", "补充说明", QuestionType.ShortText),
        ]);
        shell.ShowQuestionWizard(wizard);

        // 选择「甲」并前进 → 短文本题应自动进入提问模式（而非回落聊天提交路径）
        shell.HandleInteractionKey(Terminal.Gui.Input.Key.Enter).Should().BeTrue();
        wizard.CurrentIndex.Should().Be(1);
        shell.ChatInput.IsQuestionMode.Should().BeTrue();
    }

    [Fact]
    public void ChoiceQuestion_Esc_CancelsWizard()
    {
        var shell = CreateShell();
        var wizard = new QuestionWizard("向导", [
            new WizardQuestion("q1", "选择", QuestionType.SingleChoice, ["甲", "乙"]),
        ]);
        shell.ShowQuestionWizard(wizard);

        shell.HandleInteractionKey(Terminal.Gui.Input.Key.Esc).Should().BeTrue();
        wizard.ResultTask.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void DismissQuestionWizard_RemovesTailRegionAndRestoresInput()
    {
        var shell = CreateShell();
        var wizard = new QuestionWizard("向导", [
            new WizardQuestion("q1", "简答", QuestionType.ShortText),
        ]);
        shell.ShowQuestionWizard(wizard);
        var linesWhenShown = shell.Transcript.MessageView.TotalLines;

        shell.DismissQuestionWizard();

        shell.Transcript.MessageView.TotalLines.Should().Be(0);
        shell.ChatInput.IsQuestionMode.Should().BeFalse();
        linesWhenShown.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ShowQuestionWizard_ReplacesInlineSelector_CompletesSelectorAsDismissed()
    {
        var shell = CreateShell();
        var selector = new InlineSelector("权限", [
            new InlineSelectorOption("allow", "允许"),
            new InlineSelectorOption("deny", "拒绝"),
        ]);
        var wizard = new QuestionWizard("向导", [
            new WizardQuestion("q1", "选择", QuestionType.SingleChoice, ["甲", "乙"]),
        ]);

        shell.ShowInlineSelector(selector);
        shell.ShowQuestionWizard(wizard);

        var selectorResult = await selector.ResultTask;
        selectorResult.IsDismissed.Should().BeTrue();
        wizard.ResultTask.IsCompleted.Should().BeFalse();
        shell.Transcript.MessageView.TotalLines.Should().BeGreaterThan(0);

        // Down 只作用于向导，不再落到已替换的选择器
        selector.SelectedIndex.Should().Be(0);
        shell.HandleInteractionKey(Terminal.Gui.Input.Key.CursorDown).Should().BeTrue();
        selector.SelectedIndex.Should().Be(0);
    }

    [Fact]
    public async Task ShowInlineSelector_ReplacesQuestionWizard_CancelsWizard()
    {
        var shell = CreateShell();
        var wizard = new QuestionWizard("向导", [
            new WizardQuestion("q1", "选择", QuestionType.SingleChoice, ["甲", "乙"]),
        ]);
        var selector = new InlineSelector("权限", [
            new InlineSelectorOption("allow", "允许"),
            new InlineSelectorOption("deny", "拒绝"),
        ]);

        shell.ShowQuestionWizard(wizard);
        shell.ShowInlineSelector(selector);

        var wizardResult = await wizard.ResultTask;
        wizardResult.IsCancelled.Should().BeTrue();
        selector.ResultTask.IsCompleted.Should().BeFalse();
        shell.ChatInput.IsQuestionMode.Should().BeFalse();

        shell.HandleInteractionKey(Terminal.Gui.Input.Key.CursorDown).Should().BeTrue();
        selector.SelectedIndex.Should().Be(1);
        wizard.CurrentIndex.Should().Be(0);
    }

    [Fact]
    public void DispatchInputKey_SelectorDown_ConsumesBubbleAndMovesOnce()
    {
        var shell = CreateShell();
        var selector = new InlineSelector("权限", [
            new InlineSelectorOption("allow", "允许"),
            new InlineSelectorOption("deny", "拒绝"),
        ]);
        shell.ShowInlineSelector(selector);

        // 经 ChatInputView.OnInputKeyPress（真实输入路径），不是直接 HandleInteractionKey
        shell.ChatInput.DispatchInputKey(Terminal.Gui.Input.Key.CursorDown);
        selector.SelectedIndex.Should().Be(1);

        // 同一键若再冒泡到 ChatInputView.OnKeyDown，必须吞掉，不能再跳一格
        shell.ChatInput.DispatchBubbledKeyDown(Terminal.Gui.Input.Key.CursorDown).Should().BeTrue();
        selector.SelectedIndex.Should().Be(1);
    }

    [Fact]
    public void DispatchInputKey_UnconsumedKey_IsSwallowedWhileSelectorSuspended()
    {
        var shell = CreateShell();
        var selector = new InlineSelector("权限", [
            new InlineSelectorOption("allow", "允许"),
            new InlineSelectorOption("deny", "拒绝"),
        ]);
        shell.ShowInlineSelector(selector);

        // 普通字符不被选择器消耗，但挂起态仍必须吞掉，防止回落到聊天提交
        shell.ChatInput.DispatchInputKey(Terminal.Gui.Input.Key.A);
        selector.SelectedIndex.Should().Be(0);
        shell.ChatInput.DispatchBubbledKeyDown(Terminal.Gui.Input.Key.A).Should().BeTrue();
        selector.SelectedIndex.Should().Be(0);
    }

    [Fact]
    public void ShortTextQuestion_ShiftEnter_GoesBackAndKeepsTypedAnswer()
    {
        var shell = CreateShell();
        var wizard = new QuestionWizard("向导", [
            new WizardQuestion("q1", "第一题", QuestionType.ShortText),
            new WizardQuestion("q2", "第二题", QuestionType.ShortText),
        ]);
        shell.ShowQuestionWizard(wizard);
        wizard.SetTextAnswer("答案一");
        wizard.HandleKey(Terminal.Gui.Input.Key.Enter);
        wizard.CurrentIndex.Should().Be(1);
        shell.EnterTextModeForCurrentQuestion();
        shell.ChatInput.IsQuestionMode.Should().BeTrue();

        shell.ChatInput.SetInputText("答案二");
        shell.ChatInput.DispatchInputKey(Terminal.Gui.Input.Key.Enter.WithShift);

        wizard.CurrentIndex.Should().Be(0);
        wizard.Questions[1].Answer.Should().Be("答案二");
        shell.ChatInput.IsQuestionMode.Should().BeTrue();
    }

    [Fact]
    public async Task LongTextQuestion_CtrlEnterFinishes_PlainEnterDoesNot()
    {
        var shell = CreateShell();
        var wizard = new QuestionWizard("向导", [
            new WizardQuestion("q1", "长说明", QuestionType.LongText),
        ]);
        shell.ShowQuestionWizard(wizard);
        shell.EnterTextModeForCurrentQuestion();
        shell.ChatInput.IsLongTextQuestionMode.Should().BeTrue();

        shell.ChatInput.SetInputText("多行内容");
        shell.ChatInput.DispatchInputKey(Terminal.Gui.Input.Key.Enter);
        wizard.ResultTask.IsCompleted.Should().BeFalse("plain Enter is for newline, not submit");

        shell.ChatInput.DispatchInputKey(Terminal.Gui.Input.Key.Enter.WithCtrl);
        var result = await wizard.ResultTask.WaitAsync(TimeSpan.FromSeconds(2));
        result.IsCancelled.Should().BeFalse();
        result.Answers["q1"].Should().Be("多行内容");
    }

    [Fact]
    public async Task PlanCard_PendingApproval_EnterApproves()
    {
        var shell = CreateShell();
        var decided = new TaskCompletionSource<PlanCardDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.PlanDecisionMade += d => decided.TrySetResult(d);

        shell.ShowPlanCard("重构", [new PlanStep("拆分键路由")], PlanCardPhase.PendingApproval);
        shell.HandleInteractionKey(Terminal.Gui.Input.Key.Enter).Should().BeTrue();

        var decision = await decided.Task.WaitAsync(TimeSpan.FromSeconds(2));
        decision.Should().Be(PlanCardDecision.Approve);
    }

    [Fact]
    public async Task PlanCard_PendingApproval_EditFillsInputPrefix()
    {
        var shell = CreateShell();
        var decided = new TaskCompletionSource<PlanCardDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        shell.PlanDecisionMade += d => decided.TrySetResult(d);

        shell.ShowPlanCard("重构", [new PlanStep("拆分键路由")], PlanCardPhase.PendingApproval);
        shell.HandleInteractionKey(Terminal.Gui.Input.Key.CursorDown).Should().BeTrue();
        shell.HandleInteractionKey(Terminal.Gui.Input.Key.Enter).Should().BeTrue();

        var decision = await decided.Task.WaitAsync(TimeSpan.FromSeconds(2));
        decision.Should().Be(PlanCardDecision.Edit);
    }

    [Fact]
    public void PlanCard_Draft_DoesNotTakeOverKeyboard()
    {
        var shell = CreateShell();
        PlanCardDecision? decided = null;
        shell.PlanDecisionMade += d => decided = d;

        shell.ShowPlanCard("草稿", [new PlanStep("还在写")], PlanCardPhase.Draft);
        shell.HandleInteractionKey(Terminal.Gui.Input.Key.Enter).Should().BeFalse();
        decided.Should().BeNull();
    }

    private static ReplShell CreateShell()
    {
        var app = Substitute.For<IApplication>();
        app.Invoke(Arg.Do<Action>(action => action()));
        app.AddTimeout(Arg.Any<TimeSpan>(), Arg.Any<Func<bool>>()).Returns(true);

        var resolver = new KeybindingResolver();
        resolver.SetBindings([.. KeybindingDefaults.GetDefaultParsedBindings()]);
        var keyContextManager = new KeybindingContextManager
        {
            FocusContext = KeybindingDefaults.ContextChat,
        };

        return new ReplShell(
            app,
            version: "test",
            model: "test-model",
            sshHost: null,
            slashCommands: [],
            modeController: new WorkingModeController(),
            keyResolver: resolver,
            keyContextManager: keyContextManager,
            clipboard: null,
            historyProvider: null,
            toolNameProvider: () => []);
    }
}
