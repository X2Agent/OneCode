using OneCode.Core.Keybindings;

namespace OneCode.App.Tui;

/// <summary>
/// Main REPL shell — three-layer layout with an optional right plan sidebar:
///
/// <code>
/// │                                        │               │
/// │  Chat — sole main view (scrollable)    │  PlanSidebar  │  Dim.Fill()
/// │  (sidebar auto-shrinks the chat width  │  (auto, ≤45col│  when a plan
/// │   when a plan exists; Ctrl+G toggles)  │   Ctrl+G)     │   exists)
/// │                                        │               │
/// ├─ TodoStrip ─────────────────────────────────────────────┤  0–6 rows
/// │  ◆ 待办 (2/5) ✓ #1 …  ○ #3 …                (隐藏时 0)  │  (todos_* snapshot)
/// ├─ AgentStatusBar ───────────────────────────────────────┤  1 row
/// │  ⠋ 思考中 · Opus · 🔒 Sandbox                  BUILD   │
/// ├─ ChatInputView ────────────────────────────────────────┤  2–6 rows
/// │ > _                                                    │  (dynamic)
/// └─ SessionContextBar ────────────────────────────────────┘  1 row
/// </code>
///
/// 注意：<c>StatusBarTopGap</c> / <c>ChatInputContextGap</c> 已置 0（见 <see cref="TuiSpacing"/>），
/// 图中不再有额外空行；输入区高度按 <c>Clamp(H/6, 2, 6)</c> 动态伸缩。
/// <see cref="TodoStripView"/> 显示时从内容区借行（对话列宽度不变），
/// 空快照整体隐藏——见 <c>ReplShell.TodoPanel.cs</c>。
/// </summary>
/// <summary>ReplShell 的宿主环境：应用实例、初始模型与平台服务。</summary>
public sealed record ReplShellEnvironment(
    IApplication App,
    string InitialModel,
    OneCode.Core.IO.IClipboardService? Clipboard = null,
    OneCode.Core.Commands.IGitHelper? GitHelper = null);

/// <summary>ReplShell 的键位服务：解析器与上下文管理器成对使用。</summary>
public sealed record ReplShellKeyServices(
    KeybindingResolver Resolver,
    KeybindingContextManager ContextManager);

/// <summary>ReplShell 的输入与显示数据源（由宿主注入的动态 provider）。</summary>
public sealed record ReplShellInputSources(
    IReadOnlyList<SlashCommandEntry> SlashCommands,
    Func<IReadOnlyCollection<string>> ToolNameProvider,
    Func<IReadOnlyList<string>>? HistoryProvider = null,
    Func<bool>? GetShowThinking = null);

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
    private readonly PlanSidebarView _planSidebar;
    private readonly FrameView _completionOverlay;
    private SidebarViewBase? _sidebarHiddenForWidth;
    private bool _completionVisible;

    /// <summary>当前可见的侧栏（Plan 面板）；键盘翻页的目标。</summary>
    private SidebarViewBase? ActiveSidebar =>
        _planSidebar.Visible ? _planSidebar : null;

    // 待办横条（输入框上方全宽状态带）当前占用行数；0 = 隐藏。见 ReplShell.TodoPanel.cs。
    private int _todoStripRows;

    private int _lastShellWidth = -1;
    private int _lastShellHeight = -1;

    // 预留 1 列滚动条，与 MessageListView 绘制期 contentWidth 对齐（见 ChatTranscriptView.ContentWidth）。
    private int ContentWidth => TuiSpacing.GetContentColumnWidth(
        Math.Max(0, (_transcript.Viewport.Width > 0 ? _transcript.Viewport.Width : Viewport.Width) - 1));

    public ChatInputView ChatInput => _chatInput;
    public ChatTranscriptView Transcript => _transcript;
    public WorkingModeController ModeController => _modeController;
    public OverlayHost Overlays => _overlayHost;
    public AgentStatusBar AgentStatusBar => _agentStatusBar;
    public SessionContextBar SessionContextBar => _sessionContextBar;

    public ReplShell(
        ReplShellEnvironment environment,
        WorkingModeController modeController,
        ReplShellKeyServices keyServices,
        ReplShellInputSources inputSources)
    {
        _app = environment.App;
        _modeController = modeController;
        _keyResolver = keyServices.Resolver;
        _keyContextManager = keyServices.ContextManager;
        _clipboard = environment.Clipboard;
        _gitHelper = environment.GitHelper;

        _transcript = new ChatTranscriptView(_app, _clipboard, inputSources.GetShowThinking);
        _chatInput = new ChatInputView(
            _app,
            _modeController,
            inputSources.SlashCommands,
            inputSources.ToolNameProvider,
            _keyResolver,
            _keyContextManager,
            _clipboard,
            inputSources.HistoryProvider);

        CanFocus = true;
        TabStop = TabBehavior.NoStop;
        SetScheme(TuiStyles.Base);

        _chatInput.BottomOffset = TuiSpacing.SessionContextBarHeight + TuiSpacing.ChatInputContextGap;
        _chatInput.Height = ChatInputView.FixedHeight;
        _chatInput.Y = Pos.AnchorEnd(ChatInputView.FixedHeight + _chatInput.BottomOffset);

        // Forward global shortcuts from ChatInputView.
        // Editor consumes all keys when prompt is focused, so ReplShell.OnKeyDown
        // never fires. ChatInputView intercepts these and forwards them here.

        // 交互会话（InlineSelector/QuestionWizard 接管键盘期间）由本类统一处理：
        // ChatInputView 把挂起态/提问态的按键转发给会话（见 IInteractionSession），
        // 替代旧的松散转发事件。
        _chatInput.InteractionSession = this;

        // Shift+Up/Down or Ctrl+PgUp/PgDn — scroll conversation transcript (line-level)
        _chatInput.ScrollUpRequested += () => _transcript.MessageView.ScrollUp();
        _chatInput.ScrollDownRequested += () => _transcript.MessageView.ScrollDown();

        // PageUp/PageDown — scroll the conversation transcript (page-level)
        _chatInput.PageUpRequested += () => _transcript.MessageView.PageUp();
        _chatInput.PageDownRequested += () => _transcript.MessageView.PageDown();

        // Shift+PageUp/PageDown — scroll the active plan sidebar (page-level)
        _chatInput.SidebarPageUpRequested += () => ActiveSidebar?.PageUp();
        _chatInput.SidebarPageDownRequested += () => ActiveSidebar?.PageDown();

        // Ctrl+Shift+Left/Right — 键盘调整右侧侧边栏宽度（Plan 面板）。
        _chatInput.SidebarWiderRequested += () => AdjustSidebarWidth(SidebarViewBase.KeyboardResizeStep);
        _chatInput.SidebarNarrowerRequested += () => AdjustSidebarWidth(-SidebarViewBase.KeyboardResizeStep);
        _chatInput.SidebarToggleRequested += () => ToggleSidebarVisibility();

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
        if (!string.IsNullOrEmpty(environment.InitialModel))
            _agentStatusBar.SetModel(environment.InitialModel);

        // Activity transitions and model identity share one authoritative runtime component.
        _transcript.ActivityChanged += activity => _agentStatusBar.SetActivity(activity);

        // content zone — chat column plus the optional right plan sidebar
        _contentZone = new View
        {
            CanFocus = false,
            TabStop = TabBehavior.NoStop,
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(TuiSpacing.ContentZoneReservedBottom),
        };
        _contentZone.SetScheme(TuiStyles.Base);
        _contentZone.Add(_transcript);

        _transcript.X = 1; _transcript.Y = 0;
        _transcript.Width = Dim.Fill() - 1; _transcript.Height = Dim.Fill();

        // Plan sidebar — right-docked, hidden until a plan exists. When visible
        // it shrinks the transcript width (ApplySidebarLayout); once shown it
        // stays visible until the plan is cleared or a new session starts. The
        // separator line is a mouse drag handle: during the drag only layout
        // is refreshed (OnSidebarWidthChanged), the plan content and conversation
        // list re-render on release (OnSidebarDragEnded).
        _planSidebar = new PlanSidebarView(_app, OnSidebarWidthChanged, OnSidebarDragEnded)
        {
            Y = 0,
            Height = Dim.Fill(),
            Visible = false,
        };
        _planSidebar.X = Pos.AnchorEnd(_planSidebar.CurrentWidth);
        _contentZone.Add(_planSidebar);

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

        // 待办横条（agent todos_* 清单快照）：空快照时不可见、不占行；
        // 显示时从内容区底部借行，对话列宽度不变。见 ReplShell.TodoPanel.cs。
        _todoStrip = new TodoStripView
        {
            X = 0,
            Width = Dim.Fill(),
            Visible = false,
        };

        // assembly — no welcome view; thinking renders as a clickable
        // summary inside ChatTranscriptView, not a separate top panel.
        Add(_contentZone, _todoStrip, _agentStatusBar, _chatInput, _sessionContextBar, _overlayHost);

        Width = Dim.Fill();
        Height = Dim.Fill();

        _modeController.ModeChanged += (_, args) =>
        {
            _transcript.CurrentMode = args.CurrentMode;
            // GOAL 模式下底部栏隐藏 ctx 百分比（见 SessionContextBar.SetWorkingMode）。
            _sessionContextBar.SetWorkingMode(args.CurrentMode);
            var bannerLines = ChatBlockRenderers.RenderModeBanner(args.CurrentMode, ContentWidth);
            // Replace trailing banner in-place — stacking snapshots makes the chat
            // look one step behind the live status-bar mode during rapid Tab.
            _transcript.UpdateModeBanner(bannerLines);
        };

        _transcript.CurrentMode = _modeController.Mode;
        _sessionContextBar.SetWorkingMode(_modeController.Mode);
    }

    // Plan card interaction lives in ReplShell.PlanCard.cs:
    // 计划内容渲染在右侧 PlanSidebarView；PendingApproval 阶段弹出 InlineSelector
    // 决策面板（在对话流内），自动接管键盘（SetInteractionSuspended）。

    /// <summary>Whether the right plan sidebar is currently visible.</summary>
    internal bool IsPlanSidebarVisible => _planSidebar.Visible;

    private void SetPlanSidebarVisible(bool visible)
    {
        if (_planSidebar.Visible == visible)
            return;
        _planSidebar.Visible = visible;
        ApplySidebarLayout();
        // 对话列宽度变化：旧换行不再匹配新视口，请求按新宽度整体重渲。
        _transcript.RequestContentRerender();
    }

    /// <summary>
    /// Sidebar separator drag callback (each width change during the drag) —
    /// reflow the chat column only. Re-rendering content per mouse-move is
    /// wasted work mid-drag (and resets the sidebar scroll position).
    /// </summary>
    private void OnSidebarWidthChanged() => ApplySidebarLayout();

    /// <summary>
    /// 键盘调整当前可见侧边栏（Plan）的宽度，clamp 规则与分隔线鼠标拖拽
    /// 共用（<see cref="SidebarViewBase.AdjustWidth"/>）。宽度实际变化时按最终宽度
    /// 重渲侧边栏内容与对话列（等同一次拖拽释放，见 <see cref="OnSidebarDragEnded"/>）。
    /// </summary>
    internal void AdjustSidebarWidth(int delta)
    {
        if (!_planSidebar.Visible || !_planSidebar.AdjustWidth(delta))
            return;

        RenderActivePlanCard();
        _transcript.RequestContentRerender();
    }

    /// <summary>
    /// 切换当前侧边栏可见性（Ctrl+G，app:sidebarToggle）。
    /// 有活跃计划面板时切换其展开/隐藏；无内容时不展开空面板。
    /// </summary>
    internal void ToggleSidebarVisibility()
    {
        if (_planSidebar.Visible)
        {
            _planSidebar.Visible = false;
        }
        else
        {
            // 当前未显示：有内容才展开——Ctrl+G 虽是显式动作，但空侧栏只增视觉噪声；
            // 计划一旦出现会自动展开（见 SetPlanSidebarVisible / ShowPlanCard）。
            if (_activePlan is null)
                return;
            _planSidebar.Visible = true;
        }

        ApplySidebarLayout();
        RenderActivePlanCard();
        _transcript.RequestContentRerender();
    }

    /// <summary>
    /// Drag release callback — re-render the sidebar contents and the conversation
    /// list once at the final width (line wrapping depends on it).
    /// </summary>
    private void OnSidebarDragEnded()
    {
        RenderActivePlanCard();
        _transcript.RequestContentRerender();
    }

    /// <summary>
    /// Recomputes the transcript width so the chat column yields space to the
    /// visible sidebar (Plan) and reclaims it (minus the 1-col gutter)
    /// when it is not visible.
    /// </summary>
    private void ApplySidebarLayout()
    {
        RestoreSidebarWhenWidthAllows();

        var sidebarWidth = 0;
        if (_planSidebar.Visible)
            sidebarWidth = _planSidebar.CurrentWidth;

        if (sidebarWidth > 0 && ShouldHideSidebarForWidth(Viewport.Width, sidebarWidth))
        {
            _sidebarHiddenForWidth = _planSidebar;
            _sidebarHiddenForWidth.Visible = false;
            sidebarWidth = 0;
        }

        _transcript.Width = sidebarWidth > 0
            ? Dim.Fill() - sidebarWidth - 1
            : Dim.Fill() - 1;
        _transcript.SetNeedsLayout();
        _transcript.NotifyLayoutChanged();
        SetNeedsLayout();
        SetNeedsDraw();
    }

    /// <summary>
    /// Determines whether the sidebar must yield its space to the conversation
    /// column. A hidden sidebar is restored by <see cref="ApplySidebarLayout"/>
    /// once the terminal is wide enough again.
    /// </summary>
    internal static bool ShouldHideSidebarForWidth(int screenWidth, int sidebarWidth)
        => screenWidth > 0
            && sidebarWidth > 0
            && screenWidth < TuiSpacing.ChatColumnMinWidth + sidebarWidth + 1;

    private void RestoreSidebarWhenWidthAllows()
    {
        if (_sidebarHiddenForWidth is not { } sidebar
            || ShouldHideSidebarForWidth(Viewport.Width, sidebar.CurrentWidth))
            return;

        sidebar.Visible = true;
        _sidebarHiddenForWidth = null;
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
            // (Plan sidebar content is fixed-width and does not re-render on resize.)
            RefreshLayout();
        }
        _lastShellWidth = vp.Width;
        _lastShellHeight = vp.Height;

        return base.OnDrawingContent(context);
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
        // Input / status bars use AnchorEnd — geometry is static (Dim/Pos),
        // but Terminal.Gui needs a layout nudge after maximize/restore to
        // recompute the anchored positions.
        _chatInput.SetNeedsLayout();
        _agentStatusBar.SetNeedsLayout();
        _sessionContextBar.SetNeedsLayout();
        ApplySidebarLayout();

        // ChatTranscriptView detects resize on draw; nudge so welcome re-wraps to the new width.
        _transcript.NotifyLayoutChanged();
        _transcript.SetNeedsDraw();
        // 终端宽度变化同样使旧换行失效，请求对话内容整体重渲。
        _transcript.RequestContentRerender();

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
