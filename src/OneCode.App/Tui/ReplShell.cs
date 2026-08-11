using OneCode.Core.Keybindings;

namespace OneCode.App.Tui;

/// <summary>
/// Main REPL shell — three-layer layout:
///
/// <code>
/// │                                                        │
/// │  Chat — sole main view (scrollable)                    │  Dim.Fill()
/// │  NO sidebar, NO bottom panel (design-spec §2)          │
/// │                                                        │
/// │  (StatusBarTopGap — blank row)                         │  1 row
/// ├─ AgentStatusBar ───────────────────────────────────────┤  1 row
/// │  ⠋ 思考中 · Opus · $0.04 · Sandbox              BUILD │
/// ├─ ChatInputView ────────────────────────────────────────┤  4–5 rows
/// │ > _                                                    │
/// │  (ChatInputContextGap — blank row)                     │  1 row
/// └─ SessionContextBar ────────────────────────────────────┘  1 row
/// </code>
/// </summary>
public sealed partial class ReplShell : View
{
    private readonly AgentStatusBar _agentStatusBar;
    private readonly ChatInputView _chatInput;
    private readonly SessionContextBar _sessionContextBar;
    private readonly OverlayHost _overlayHost;
    private readonly WorkingModeController _modeController;
    private readonly IApplication _app;
    private readonly KeybindingResolver _keyResolver;
    private readonly KeybindingContextManager _keyContextManager;
    private readonly OneCode.Core.IO.IClipboardService? _clipboard;
    private readonly OneCode.Core.Commands.IGitHelper? _gitHelper;

    private readonly View _contentZone;
    private readonly ChatTranscriptView _transcript;
    private readonly FrameView _completionOverlay;
    private bool _completionVisible;

    private PlanCardState? _activePlan;

    private int _lastShellWidth = -1;
    private int _lastShellHeight = -1;

    private int ContentWidth => TuiSpacing.GetContentColumnWidth(
        _transcript.Viewport.Width > 0 ? _transcript.Viewport.Width : Viewport.Width);

    public ChatInputView ChatInput => _chatInput;
    public ChatTranscriptView Transcript => _transcript;
    public WorkingModeController ModeController => _modeController;
    public OverlayHost Overlays => _overlayHost;
    public AgentStatusBar AgentStatusBar => _agentStatusBar;
    public SessionContextBar SessionContextBar => _sessionContextBar;

    /// <summary>Raised when the user approves/rejects/edits a plan card via keyboard.</summary>
    public event Action<PlanCardDecision>? PlanDecisionMade;

    public ReplShell(IApplication app, string version, string model, string? sshHost,
        IReadOnlyList<SlashCommandEntry> slashCommands,
        WorkingModeController modeController,
        KeybindingResolver keyResolver,
        KeybindingContextManager keyContextManager,
        OneCode.Core.IO.IClipboardService? clipboard,
        Func<IReadOnlyList<string>>? historyProvider,
        Func<IReadOnlyCollection<string>> toolNameProvider,
        Func<bool>? getShowThinking = null,
        OneCode.Core.Commands.IGitHelper? gitHelper = null)
    {
        _app = app;
        _modeController = modeController;
        _keyResolver = keyResolver;
        _keyContextManager = keyContextManager;
        _clipboard = clipboard;
        _gitHelper = gitHelper;

        _transcript = new ChatTranscriptView(app, clipboard, getShowThinking);
        _chatInput = new ChatInputView(
            app,
            _modeController,
            slashCommands,
            toolNameProvider,
            keyResolver,
            keyContextManager,
            clipboard,
            historyProvider);

        CanFocus = true;
        TabStop = TabBehavior.NoStop;
        SetScheme(TuiTheme.Base);

        _chatInput.BottomOffset = TuiSpacing.SessionContextBarHeight + TuiSpacing.ChatInputContextGap;
        _chatInput.Y = Pos.AnchorEnd(ChatInputView.MaxHeight + _chatInput.BottomOffset);

        // Forward global shortcuts from ChatInputView.
        // Editor consumes all keys when prompt is focused, so ReplShell.OnKeyDown
        // never fires. ChatInputView intercepts these and forwards them here.

        // When interaction is suspended (InlineSelector active for permission prompts),
        // Editor still has focus and consumes arrow/Enter/Esc keys. Forward them here
        // so they reach the active InlineSelector.
        _chatInput.InteractionSuspendedKeyForwarded += OnInteractionSuspendedKey;
        _chatInput.QuestionPreviousRequested += OnQuestionPreviousRequested;
        _chatInput.QuestionCancelRequested += OnQuestionCancelRequested;
        _chatInput.QuestionLongTextNavigationRequested += OnQuestionLongTextNavigationRequested;
        _chatInput.QuestionTextNavigationRequested += OnQuestionTextNavigationRequested;

        // Shift+Up/Down or Ctrl+PgUp/PgDn — scroll conversation transcript (line-level)
        _chatInput.ScrollUpRequested += () => _transcript.MessageView.ScrollUp();
        _chatInput.ScrollDownRequested += () => _transcript.MessageView.ScrollDown();

        // PageUp/PageDown — scroll the conversation transcript (page-level)
        _chatInput.PageUpRequested += () => _transcript.MessageView.PageUp();
        _chatInput.PageDownRequested += () => _transcript.MessageView.PageDown();

        // SessionContextBar sits at the bottom; ChatInputView and AgentStatusBar anchor above it.
        _sessionContextBar = new SessionContextBar()
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1, // always visible (shows workspace + stats)
        };

        _agentStatusBar = new AgentStatusBar(_app, _modeController)
        {
            X = 0,
            Y = Pos.Top(_chatInput) - 1,
            Width = Dim.Fill(),
            Height = 1,
        };
        if (!string.IsNullOrEmpty(model))
            _agentStatusBar.SetModel(model);

        // Activity transitions and model identity share one authoritative runtime component.
        _transcript.ActivityChanged += activity => _agentStatusBar.SetActivity(activity);

        // content zone — full width, no separate thinking sidebar
        _contentZone = new View
        {
            CanFocus = false,
            TabStop = TabBehavior.NoStop,
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill() - TuiSpacing.ContentZoneReservedBottom,
        };
        _contentZone.SetScheme(TuiTheme.Base);
        _contentZone.Add(_transcript);

        _transcript.X = 1; _transcript.Y = 0;
        _transcript.Width = Dim.Fill() - 1; _transcript.Height = Dim.Fill();

        // overlay host (full-screen, on top)
        _overlayHost = new OverlayHost(this)
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Visible = false,
        };

        // completion overlay
        _completionOverlay = _chatInput.CompletionFrame;
        _chatInput.CompletionStateChanged += OnCompletionStateChanged;

        // assembly — NO sidebar, NO welcome view
        // Thinking is rendered in ChatTranscriptView as one clickable summary.
        // Do not add a top ThinkingPanel: it would duplicate the conversation thinking block.
        Add(_contentZone, _agentStatusBar, _chatInput, _sessionContextBar, _overlayHost);

        Width = Dim.Fill();
        Height = Dim.Fill();

        _modeController.ModeChanged += (_, args) =>
        {
            _transcript.CurrentMode = args.CurrentMode;
            var bannerLines = ChatBlockRenderers.RenderModeBanner(args.CurrentMode, args.CurrentStrategy);
            // Replace trailing banner in-place — stacking snapshots makes the chat
            // look one step behind the live status-bar mode during rapid Tab.
            _transcript.UpdateModeBanner(bannerLines);
        };

        _transcript.CurrentMode = _modeController.Mode;
    }

    /// <summary>
    /// Detects terminal resize by comparing the current viewport with the last
    /// known dimensions. When a change is detected, forces a full layout refresh
    /// so all child regions (transcript, agent status, chat input, session context,
    /// and overlays) recompute their positions and dimensions.
    /// </summary>
    protected override bool OnDrawingContent(DrawContext? context)
    {
        var vp = Viewport;
        if (_lastShellWidth >= 0 && (_lastShellWidth != vp.Width || _lastShellHeight != vp.Height))
        {
            // Terminal was resized — force a layout refresh. SetNeedsLayout
            // only sets a flag (does not execute layout synchronously), so
            // this is safe to call during the draw pass. The actual relayout
            // happens on the next main-loop iteration.
            RefreshLayout();
            RenderActivePlanCard();
        }
        _lastShellWidth = vp.Width;
        _lastShellHeight = vp.Height;

        return base.OnDrawingContent(context);
    }

    // Plan card interaction (design-spec §4.2)
    // PendingApproval 阶段弹出 InlineSelector，自动接管键盘（SetInteractionSuspended）。
    // Draft 阶段仅展示卡片不弹决策面板。

    /// <summary>
    /// Show a plan card. <see cref="PlanCardPhase.PendingApproval"/> 额外弹出
    /// InlineSelector 决策面板（批准/拒绝/修改），自动接管键盘；<see cref="PlanCardPhase.Draft"/>
    /// 仅展示卡片，不响应任何决策输入。
    /// </summary>
    public void ShowPlanCard(
        string title,
        IReadOnlyList<PlanStep> steps,
        PlanCardPhase phase,
        string? markdown = null)
    {
        _activePlan = new PlanCardState(title, steps.ToList(), phase, markdown);
        RenderActivePlanCard();

        if (phase is PlanCardPhase.Completed or PlanCardPhase.Failed or PlanCardPhase.Cancelled)
        {
            // Seal the terminal card as transcript history so a later plan starts a new card.
            _activePlan = null;
            _transcript.ClearActivePlanCard();
            return;
        }

        if (phase != PlanCardPhase.PendingApproval)
        {
            // Draft：仅展示卡片，不弹决策面板。用户无法提前决策，消除 _pendingDecision 竞态。
            return;
        }

        // PendingApproval：弹出 InlineSelector 决策面板。
        // InlineSelector 通过 _chatInput.SetInteractionSuspended(true) 自动接管键盘，
        // 无需依赖 prompt 失焦——彻底消除焦点冲突。
        var options = new List<InlineSelectorOption>
        {
            new("approve", "批准并执行", "冻结当前计划，切换到 Build 模式立即开始执行"),
            new("edit", "输入修改意见", "返回输入框，用自然语言说明要调整的内容"),
            new("reject", "拒绝计划", "保持在 Plan 模式，重新规划当前方案"),
        };
        var selector = new InlineSelector("请审批以上计划", options);
        ShowInlineSelector(selector);

        _ = selector.ResultTask.ContinueWith(t =>
        {
            _app.Invoke(() =>
            {
                DismissInlineSelector();
                // Esc 取消（Dismissed）视为 Reject——保持 Plan 模式，等待 LLM 修订
                var decision = (t.IsCompletedSuccessfully && !t.Result.IsDismissed)
                    ? t.Result.SelectedId switch
                    {
                        "approve" => PlanCardDecision.Approve,
                        "reject" => PlanCardDecision.Reject,
                        "edit" => PlanCardDecision.Edit,
                        _ => PlanCardDecision.Reject,
                    }
                    : PlanCardDecision.Reject;

                PlanDecisionMade?.Invoke(decision);
                if (decision == PlanCardDecision.Edit)
                {
                    _chatInput.SetInputText("请按以下意见修改计划：");
                    _chatInput.FocusInput();
                }
                ClearPlan(sealCard: false);
            });
        }, TaskScheduler.Default);
    }

    private void RenderActivePlanCard()
    {
        if (_activePlan is not { } plan)
            return;

        var displayTitle = plan.Phase switch
        {
            PlanCardPhase.Draft => $"{plan.Title} · 正在整理",
            PlanCardPhase.Finalizing => $"{plan.Title} · 正在确认",
            PlanCardPhase.PendingApproval => $"{plan.Title} · 等待审批",
            PlanCardPhase.StartingExecution => $"{plan.Title} · 准备执行",
            PlanCardPhase.Executing => $"{plan.Title} · 正在执行",
            PlanCardPhase.Verifying => $"{plan.Title} · 正在验证",
            PlanCardPhase.Completed => $"{plan.Title} · 已完成",
            PlanCardPhase.Failed => $"{plan.Title} · 执行失败",
            PlanCardPhase.Cancelled => $"{plan.Title} · 已取消",
            _ => plan.Title,
        };
        var lines = ChatBlockRenderers.RenderPlanCard(
            displayTitle,
            plan.Steps,
            ContentWidth,
            showActionButtons: false,
            showApprovalGuidance: plan.Phase == PlanCardPhase.PendingApproval,
            markdown: plan.Markdown);
        _transcript.UpdatePlanCard(lines);
    }

    /// <summary>Clears the approval interaction; optionally seals the card as transcript history.</summary>
    public void ClearPlan(bool sealCard = true)
    {
        _activePlan = null;
        if (sealCard)
            _transcript.ClearActivePlanCard();
    }

    // Public API

    public void UpdateHeader(string model)
    {
        _agentStatusBar.SetModel(model);
        SetNeedsDraw();
    }

    public void SetAgentBusy(bool busy, string initialActivity = "处理中")
    {
        if (busy)
            _agentStatusBar.SetActivity(initialActivity);
        _agentStatusBar.SetBusy(busy);
    }

    public void FocusChatInput()
    {
        _chatInput.FocusInput();
    }

    /// <summary>Refreshes the conversation layout and re-clamps open overlays.</summary>
    public void RefreshLayout()
    {
        _contentZone.Y = 0;
        _contentZone.Height = Dim.Fill() - TuiSpacing.ContentZoneReservedBottom;

        // Input / status bars use AnchorEnd — force them to recompute after maximize.
        _chatInput.SetNeedsLayout();
        _agentStatusBar.SetNeedsLayout();
        _sessionContextBar.SetNeedsLayout();

        // ChatTranscriptView detects resize on draw; nudge so welcome re-wraps to the new width.
        _transcript.NotifyLayoutChanged();
        _transcript.SetNeedsDraw();

        if (_overlayHost.IsOverlayVisible)
            _overlayHost.RepositionAll();

        SetNeedsLayout();
        SetNeedsDraw();
    }

    /// <summary>
    /// Refresh the session context bar (git branch/worktree info and consumption metrics).
    /// SessionContextBar is always visible.
    /// </summary>
    public async Task RefreshSessionContextBarAsync(string? workingDirectory = null, CancellationToken ct = default)
    {
        await _sessionContextBar.RefreshAsync(_gitHelper, workingDirectory, ct).ConfigureAwait(false);

        // Keep ChatInputView above the always-visible SessionContextBar.
        _chatInput.BottomOffset = TuiSpacing.SessionContextBarHeight + TuiSpacing.ChatInputContextGap;
        _chatInput.SetNeedsDraw();

        SetNeedsLayout();
        SetNeedsDraw();
    }
}
