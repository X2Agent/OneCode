using OneCode.Core.Keybindings;

namespace OneCode.App.Tui;

/// <summary>
/// Main REPL shell — three-layer layout with an optional right plan sidebar:
///
/// <code>
/// │                                        │               │
/// │  Chat — sole main view (scrollable)    │  PlanSidebar  │  Dim.Fill()
/// │  (sidebar auto-shrinks the chat width  │  (auto, 42col │  when a plan
/// │   when a plan exists; Ctrl+G toggles)  │   Ctrl+G)     │   exists)
/// │                                        │               │
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
    private readonly PlanSidebarView _planSidebar;
    private readonly FrameView _completionOverlay;
    private bool _completionVisible;

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

        // content zone — chat column plus the optional right plan sidebar
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

        // Plan sidebar — right-docked, hidden until a plan exists. When visible
        // it shrinks the transcript width (ApplySidebarLayout). Ctrl+G toggles;
        // the separator line is a mouse drag handle: during the drag only layout
        // is refreshed (OnSidebarWidthChanged), the plan content and conversation
        // list re-render on release (OnSidebarDragEnded).
        _planSidebar = new PlanSidebarView(app, OnSidebarWidthChanged, OnSidebarDragEnded)
        {
            Y = 0,
            Height = Dim.Fill(),
            Visible = false,
        };
        _planSidebar.X = Pos.AnchorEnd(_planSidebar.CurrentWidth);
        _contentZone.Add(_planSidebar);
        _chatInput.TogglePlanPanelRequested += TogglePlanSidebar;

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

        // assembly — no welcome view; thinking renders as a clickable
        // summary inside ChatTranscriptView, not a separate top panel.
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

    // Plan card interaction (design-spec §4.2) lives in ReplShell.PlanCard.cs:
    // 计划内容渲染在右侧 PlanSidebarView；PendingApproval 阶段弹出 InlineSelector
    // 决策面板（在对话流内），自动接管键盘（SetInteractionSuspended）。

    /// <summary>Whether the right plan sidebar is currently visible.</summary>
    internal bool IsPlanSidebarVisible => _planSidebar.Visible;

    /// <summary>
    /// Toggles the plan sidebar (Ctrl+G). Only meaningful while a plan exists —
    /// lets the user reclaim full chat width without losing the plan content.
    /// </summary>
    internal void TogglePlanSidebar()
    {
        if (_activePlan is null)
            return;
        SetPlanSidebarVisible(!_planSidebar.Visible);
    }

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
    /// Drag release callback — re-render the plan content and the conversation
    /// list once at the final width (line wrapping depends on it).
    /// </summary>
    private void OnSidebarDragEnded()
    {
        RenderActivePlanCard();
        _transcript.RequestContentRerender();
    }

    /// <summary>
    /// Recomputes the transcript width so the chat column yields space to the
    /// sidebar when visible and reclaims it (minus the 1-col gutter) when not.
    /// </summary>
    private void ApplySidebarLayout()
    {
        _transcript.Width = _planSidebar.Visible
            ? Dim.Fill() - _planSidebar.CurrentWidth - 1
            : Dim.Fill() - 1;
        _transcript.SetNeedsLayout();
        _transcript.NotifyLayoutChanged();
        SetNeedsLayout();
        SetNeedsDraw();
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
