using NSubstitute;
using OneCode.App.Tui;
using OneCode.Core.Commands;
using OneCode.Core.Coordinator;
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
        linesWhenShown.Should().BeGreaterThanOrEqualTo(2, "选择器至少渲染标题与选项行");

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
        linesWhenShown.Should().BeGreaterThanOrEqualTo(2, "向导至少渲染标题与题目行");
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
        shell.Transcript.MessageView.TotalLines.Should().BeGreaterThanOrEqualTo(2, "向导替换选择器后交互区域仍有内容");

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
    public void DispatchInputKey_ScrollKeysPassThroughWhileSelectorSuspended()
    {
        // 审批计划卡（挂起态）恰恰需要翻阅长对话/长计划：PageDown / Ctrl+PgDn
        // 等转录滚动键必须放行到滚动分发，不得被挂起分支吞掉。
        var shell = CreateShell();
        shell.Transcript.MessageView.AppendLines(
            Enumerable.Range(1, 30)
                .Select(i => FormattedLine.Plain($"line {i}", Terminal.Gui.Drawing.Color.White))
                .ToList());
        shell.Transcript.MessageView.DispatchKeyDown(Terminal.Gui.Input.Key.Home);
        shell.Transcript.MessageView.ScrollOffset.Should().Be(0);

        var selector = new InlineSelector("权限", [
            new InlineSelectorOption("allow", "允许"),
            new InlineSelectorOption("deny", "拒绝"),
        ]);
        shell.ShowInlineSelector(selector);

        shell.ChatInput.DispatchInputKey(Terminal.Gui.Input.Key.PageDown);
        var offsetAfterPageDown = shell.Transcript.MessageView.ScrollOffset;
        offsetAfterPageDown.Should().BeGreaterThan(0, "挂起态 PageDown 放行并驱动对话区下翻");

        shell.ChatInput.DispatchInputKey(Terminal.Gui.Input.Key.PageDown.WithCtrl);
        shell.Transcript.MessageView.ScrollOffset.Should().BeGreaterThan(
            offsetAfterPageDown, "挂起态 Ctrl+PageDown 放行（3 行小步滚动）");
    }

    [Fact]
    public void DispatchInputKey_ShiftPageDown_RoutesToSidebar()
    {
        // 分区滚动：Shift+PageDown 翻右侧计划侧栏，不触发主对话区翻页。
        var shell = CreateShell();
        shell.Transcript.MessageView.AppendLines(
            Enumerable.Range(1, 30)
                .Select(i => FormattedLine.Plain($"line {i}", Terminal.Gui.Drawing.Color.White))
                .ToList());
        shell.Transcript.MessageView.DispatchKeyDown(Terminal.Gui.Input.Key.Home);

        shell.ShowPlanCard(
            "重构",
            Enumerable.Range(1, 20).Select(i => new PlanStep($"步骤 {i}")).ToList(),
            PlanCardPhase.Finalizing);
        shell.PlanSidebar.ScrollUp(100);
        shell.PlanSidebar.ScrollOffset.Should().Be(0);

        shell.ChatInput.DispatchInputKey(Terminal.Gui.Input.Key.PageDown.WithShift);

        shell.PlanSidebar.ScrollOffset.Should().BeGreaterThan(0, "Shift+PageDown 路由到侧栏翻页");
        shell.Transcript.MessageView.ScrollOffset.Should().Be(0, "不得误触主对话区翻页");
    }

    [Fact]
    public void DispatchInputKey_ShiftPageUp_PassesThroughWhileSelectorSuspended()
    {
        // 审批计划卡（挂起态）时 Shift+PageUp 仍可向上翻阅右侧计划。
        var shell = CreateShell();
        shell.ShowPlanCard(
            "重构",
            Enumerable.Range(1, 20).Select(i => new PlanStep($"步骤 {i}")).ToList(),
            PlanCardPhase.Finalizing);
        shell.PlanSidebar.ScrollDown(10);
        var selector = new InlineSelector("权限", [
            new InlineSelectorOption("allow", "允许"),
            new InlineSelectorOption("deny", "拒绝"),
        ]);
        shell.ShowInlineSelector(selector);

        shell.ChatInput.DispatchInputKey(Terminal.Gui.Input.Key.PageUp.WithShift);

        shell.PlanSidebar.IsAutoScroll.Should().BeFalse("挂起态侧栏上翻页放行并脱离底部跟随");
        selector.SelectedIndex.Should().Be(0, "翻页不得移动选择器高亮");
    }

    [Fact]
    public void ShellKeyDown_ScrollActions_RouteToTranscriptWithoutInputFocus()
    {
        // 焦点异常离开输入框时的兜底：ReplShell.OnKeyDown 也要分发 chat 滚动。
        var shell = CreateShell();

        shell.DispatchKeyDown(Terminal.Gui.Input.Key.PageDown).Should().BeTrue();
        shell.DispatchKeyDown(Terminal.Gui.Input.Key.PageUp).Should().BeTrue();
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
        shell.ChatInput.CurrentText.Should().Be("答案一", "回退到短文本上一题时应把已填答案恢复到输入框");
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
        shell.IsPlanSidebarVisible.Should().BeTrue("选择修改意见时，侧边栏必须保持展开可见供用户对照");
        shell.ActivePlan.Should().NotBeNull();
        shell.ActivePlan!.Title.Should().Contain("待修改");
    }

    [Fact]
    public void PlanCard_NonApproval_DoesNotTakeOverKeyboard()
    {
        var shell = CreateShell();
        PlanCardDecision? decided = null;
        shell.PlanDecisionMade += d => decided = d;

        shell.ShowPlanCard("草稿", [new PlanStep("还在写")], PlanCardPhase.Finalizing);
        shell.HandleInteractionKey(Terminal.Gui.Input.Key.Enter).Should().BeFalse();
        decided.Should().BeNull();
    }

    // 侧边栏展示模式：计划内容渲染在右侧面板而非对话流。
    // 回归防护：新计划出现自动展开侧边栏；对话流不再出现 plan card 行。
    [Fact]
    public void PlanSidebar_NewPlan_AutoShowsWithoutTranscriptCard()
    {
        var shell = CreateShell();

        shell.ShowPlanCard("重构", [new PlanStep("第一步")], PlanCardPhase.Finalizing);

        shell.IsPlanSidebarVisible.Should().BeTrue("新计划出现时侧边栏自动展开");
        shell.Transcript.MessageView.TotalLines.Should().Be(0,
            "计划内容不进入对话流");
    }

    // 侧边栏一经展开保持可见：同一计划的后续更新不收起，
    // PendingApproval 强制展开（审批必须能看到完整计划）
    [Fact]
    public void PlanSidebar_PendingApproval_KeepsSidebarVisible()
    {
        var shell = CreateShell();
        shell.ShowPlanCard("重构", [new PlanStep("第一步")], PlanCardPhase.Finalizing);
        shell.IsPlanSidebarVisible.Should().BeTrue("计划出现时侧边栏展开并保持可见");

        shell.ShowPlanCard("重构", [new PlanStep("第一步")], PlanCardPhase.PendingApproval);
        shell.IsPlanSidebarVisible.Should().BeTrue("审批阶段侧边栏保持可见");
    }

    [Fact]
    public void EscHierarchy_Overlays_DismissesFirst()
    {
        var shell = CreateShell();
        var overlay = new Terminal.Gui.ViewBase.View { Width = 10, Height = 10 };
        shell.Overlays.Push(overlay);
        shell.Overlays.IsOverlayVisible.Should().BeTrue();

        shell.DispatchKeyDown(Terminal.Gui.Input.Key.Esc);

        shell.Overlays.IsOverlayVisible.Should().BeFalse("按 Esc 优先关闭顶层 Overlay");
    }

    [Fact]
    public void EscHierarchy_ActiveInteraction_Dismisses()
    {
        var shell = CreateShell();
        var selector = new InlineSelector("测试", [new InlineSelectorOption("1", "选项")]);
        shell.ShowInlineSelector(selector);
        shell.HasActiveInteractionSession().Should().BeTrue();

        shell.DispatchKeyDown(Terminal.Gui.Input.Key.Esc);

        shell.HasActiveInteractionSession().Should().BeFalse("在无 Overlay 时按 Esc 关闭内联选择器");
    }

    [Fact]
    public void EscHierarchy_ChatInput_ClearsTextWhenIdle()
    {
        var shell = CreateShell();
        shell.ChatInput.SetText("some text");
        shell.ChatInput.CurrentText.Should().Be("some text");

        shell.ChatInput.DispatchInputKey(Terminal.Gui.Input.Key.Esc);

        shell.ChatInput.CurrentText.Should().BeEmpty("输入框有文字时按 Esc 清空内容");
    }

    [Fact]
    public void SidebarToggle_CtrlO_TogglesTodoStripAndNeverHidesPlanSidebar()
    {
        var shell = CreateShell();
        shell.ShowPlanCard("重构", [new PlanStep("测试")], PlanCardPhase.Finalizing);
        shell.ShowTodoPanel([
            new TodoListItem(1, "独立待办事项", null, IsComplete: false),
        ]);
        shell.IsPlanSidebarVisible.Should().BeTrue();
        shell.TodoStrip.IsExpanded.Should().BeFalse();

        shell.ChatInput.DispatchInputKey(Terminal.Gui.Input.Key.O.WithCtrl);
        shell.IsPlanSidebarVisible.Should().BeTrue("PlanSidebar 遵循纯数据驱动显隐，按 Ctrl+O 绝不隐藏侧边栏");
        shell.TodoStrip.IsExpanded.Should().BeTrue("Ctrl+O 专用于展开底部 TodoStrip 详情");

        shell.ChatInput.DispatchInputKey(Terminal.Gui.Input.Key.O.WithCtrl);
        shell.IsPlanSidebarVisible.Should().BeTrue();
        shell.TodoStrip.IsExpanded.Should().BeFalse("再次按 Ctrl+O 应收起底部 TodoStrip 详情");
    }

    [Fact]
    public void SidebarToggle_CtrlO_DoesNotExpandEmptySidebarOrTodoStrip()
    {
        var shell = CreateShell();
        shell.IsPlanSidebarVisible.Should().BeFalse();
        shell.TodoStrip.IsExpanded.Should().BeFalse();

        shell.ToggleSidebarVisibility();

        shell.IsPlanSidebarVisible.Should().BeFalse("无内容时 Ctrl+O 不应展开空侧栏");
        shell.TodoStrip.IsExpanded.Should().BeFalse("无待办内容时 Ctrl+O 不应展开空 TodoStrip");
    }

    [Fact]
    public void ModalBarrier_WhenOverlayVisible_BlocksAllBackgroundActionsAndExitsSearch()
    {
        var shell = CreateShell();
        shell.Transcript.MessageView.AppendLines([
            FormattedLine.FromSegmentsWithTag(
                [new LineSegment("▸ Thought:", Terminal.Gui.Drawing.Color.Gray)],
                new ThinkingLineTag("hidden thought", IsExpanded: false)),
        ]);
        shell.ShowTodoPanel([
            new TodoListItem(1, "待办", null, IsComplete: false),
        ]);
        shell.ChatInput.SetText("draft");
        shell.ChatInput.BeginSearchMode();
        shell.ChatInput.IsSearchMode.Should().BeTrue();

        var overlay = new Terminal.Gui.ViewBase.View { Width = 10, Height = 10, CanFocus = true };
        shell.Overlays.Push(overlay);

        shell.ChatInput.IsSearchMode.Should().BeFalse("打开模态弹层时必须自动退出会话内搜索并恢复草稿");
        shell.ChatInput.CurrentText.Should().Be("draft");

        // 模态结界：无论经输入框还是经 ReplShell 派发，均不得穿透触发底层搜索、TodoStrip 或折叠详情
        shell.ChatInput.DispatchInputKey(Terminal.Gui.Input.Key.F.WithCtrl);
        shell.ChatInput.DispatchInputKey(Terminal.Gui.Input.Key.O.WithCtrl);
        shell.ChatInput.DispatchInputKey(Terminal.Gui.Input.Key.E.WithCtrl);
        shell.DispatchKeyDown(Terminal.Gui.Input.Key.F.WithCtrl).Should().BeTrue();
        shell.DispatchKeyDown(Terminal.Gui.Input.Key.O.WithCtrl).Should().BeTrue();
        shell.DispatchKeyDown(Terminal.Gui.Input.Key.E.WithCtrl).Should().BeTrue();

        shell.ChatInput.IsSearchMode.Should().BeFalse("弹层打开期间严禁进入搜索模式");
        shell.TodoStrip.IsExpanded.Should().BeFalse("弹层打开期间严禁穿透切换 TodoStrip");
        shell.Transcript.MessageView.RenderedLines.Should().NotContain(
            l => l.Contains("hidden thought", StringComparison.Ordinal),
            "弹层打开期间严禁穿透展开底层折叠块");
    }

    [Fact]
    public void SuspendedSelector_AllowsReadOnlyInspectionWhitelist()
    {
        var shell = CreateShell();
        shell.Transcript.MessageView.AppendLines([
            FormattedLine.FromSegmentsWithTag(
                [new LineSegment("▸ Tool:", Terminal.Gui.Drawing.Color.Gray)],
                new ThinkingLineTag("tool arguments detail", IsExpanded: false)),
        ]);
        shell.ShowTodoPanel([
            new TodoListItem(1, "独立待办", null, IsComplete: false),
        ]);

        var selector = new InlineSelector("审批工具", [
            new InlineSelectorOption("allow", "允许"),
            new InlineSelectorOption("deny", "拒绝"),
        ]);
        shell.ShowInlineSelector(selector);

        // 挂起态下按 Ctrl+E 展开工具详情、按 Ctrl+O 展开待办详情：均在白名单内放行
        shell.ChatInput.DispatchInputKey(Terminal.Gui.Input.Key.E.WithCtrl);
        shell.ChatInput.DispatchInputKey(Terminal.Gui.Input.Key.O.WithCtrl);

        shell.Transcript.MessageView.RenderedLines.Should().Contain(
            l => l.Contains("tool arguments detail", StringComparison.Ordinal),
            "审批挂起态下 Ctrl+E 必须放行以检视工具入参详情");
        shell.TodoStrip.IsExpanded.Should().BeTrue("审批挂起态下 Ctrl+O 必须放行以检视待办详情");
        selector.SelectedIndex.Should().Be(0, "只读检视动作不得干扰选择器选中项");
        selector.ResultTask.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void SearchMode_IsMutuallyExclusiveWithInteractionStates()
    {
        var shell = CreateShell();
        shell.ChatInput.SetText("my draft");
        shell.ChatInput.BeginSearchMode();
        shell.ChatInput.IsSearchMode.Should().BeTrue();

        var selector = new InlineSelector("审批", [new InlineSelectorOption("1", "允许")]);
        shell.ShowInlineSelector(selector);

        shell.ChatInput.IsSearchMode.Should().BeFalse("进入选择器挂起态时必须自动退出搜索模式");
        shell.ChatInput.CurrentText.Should().Be("my draft", "退出搜索模式时必须恢复原草稿");

        // 挂起态下尝试再次进入搜索模式应被拒绝
        shell.ChatInput.BeginSearchMode();
        shell.ChatInput.IsSearchMode.Should().BeFalse();
    }

    [Fact]
    public void ChatTextEditor_IsScrollKey_InterceptsAltUpDownForSidebarScroll()
    {
        ChatTextEditor.IsScrollKey(Terminal.Gui.Input.Key.CursorUp.WithAlt).Should().BeTrue();
        ChatTextEditor.IsScrollKey(Terminal.Gui.Input.Key.CursorDown.WithAlt).Should().BeTrue();
        ChatTextEditor.IsScrollKey(Terminal.Gui.Input.Key.CursorUp).Should().BeFalse();
    }

    [Fact]
    public void Overlays_ReviewResumeAndTrust_SupportSelectorAndTabFocusRing()
    {
        // 1. ReviewOverlay 支持 selector:next / selector:previous / selector:confirm
        ReviewFileEntry? selectedFile = null;
        var review = new ReviewOverlay([
            new ReviewFileEntry("a.cs", 10, 2, "M"),
            new ReviewFileEntry("b.cs", 5, 1, "M"),
        ]);
        review.FileSelected += f => selectedFile = f;
        review.ListView.SelectedItem.Should().Be(0);
        review.NewKeyDownEvent(Terminal.Gui.Input.Key.CursorDown).Should().BeTrue();
        review.ListView.SelectedItem.Should().Be(1);
        review.NewKeyDownEvent(Terminal.Gui.Input.Key.Enter).Should().BeTrue();
        selectedFile?.Path.Should().Be("b.cs");

        // 2. ResumeChooserOverlay 支持显式 Tab / Shift+Tab 焦点环
        var resume = new ResumeChooserOverlay([
            new SessionEntry("s1", "会话一", "gpt-5", "build", 3, DateTimeOffset.UtcNow),
        ]);
        var host = new OverlayHost(() => { });
        host.Push(resume);
        resume.ListView.HasFocus.Should().BeTrue();
        resume.NewKeyDownEvent(Terminal.Gui.Input.Key.Tab).Should().BeTrue();
        resume.NewButton.HasFocus.Should().BeTrue();
        resume.NewKeyDownEvent(Terminal.Gui.Input.Key.Tab).Should().BeTrue();
        resume.ResumeButton.HasFocus.Should().BeTrue();
        resume.NewKeyDownEvent(Terminal.Gui.Input.Key.Tab.WithShift).Should().BeTrue();
        resume.NewButton.HasFocus.Should().BeTrue();
        host.Pop();

        // 3. TrustOverlay 支持 Tab / 左右方向键在「是 / 否」按钮间切换焦点
        var trust = new TrustOverlay("E:\\OneCode", isHomeDir: false);
        host.Push(trust);
        trust.YesButton.HasFocus.Should().BeTrue();
        trust.NewKeyDownEvent(Terminal.Gui.Input.Key.Tab).Should().BeTrue();
        trust.NoButton.HasFocus.Should().BeTrue();
        trust.NewKeyDownEvent(Terminal.Gui.Input.Key.CursorLeft).Should().BeTrue();
        trust.YesButton.HasFocus.Should().BeTrue();
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
            new ReplShellEnvironment(app, "test-model"),
            new WorkingModeController(),
            new ReplShellKeyServices(resolver, keyContextManager),
            new ReplShellInputSources([]));
    }
}
