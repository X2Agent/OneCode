using OneCode.Core.Keybindings;
using OneCode.Core.Models;

namespace OneCode.App.Tui;

/// <summary>
/// Root Terminal.Gui <see cref="Window"/> — coordinates the transcript,
/// unified agent status, chat input, session context, and overlay regions.
/// Threading: all mutating UI calls happen on the Terminal.Gui main-loop
/// thread; background work is dispatched via the instance-based
/// <c>IApplication.Invoke</c>.
/// </summary>
public sealed partial class OneCodeToplevel : Window
{
    private readonly TuiContext _ctx;
    private readonly IApplication _app;
    private readonly ReplShell _shell;
    private readonly TranscriptEventPresenter _transcriptPresenter;
    private Func<CancellationToken, Task<string?>>? _showResumeChooserAsync;
    private Func<string, CancellationToken, Task>? _resumeSessionAsync;
    private Func<CancellationToken, Task<bool>>? _showSettingsOverlayAsync;
    private Func<CancellationToken, Task<string>>? _applySettingsAsync;

    private CancellationTokenSource _queryCts = new();
    private bool _isQueryRunning;
    /// <summary>
    /// Set when the user message has already been rendered in the transcript
    /// (by OnUserSubmitted for normal/immediate submit, or DrainInputQueueAsync
    /// when draining queued input). RunQueryAsync/RunCommandPromptAsync check this
    /// to avoid double-rendering.
    /// </summary>
    private bool _userMessageShown;
    /// <summary>
    /// Set when Esc/chat:killAgents already wrote the Chinese interrupt line;
    /// suppresses duplicate "(cancelled)" from query OCE handlers.
    /// </summary>
    private bool _userInterruptNotified;
    private int _inputTokens;
    private int _outputTokens;
    private int _cacheReadTokens;
    private int _cacheWriteTokens;
    private int _turnNumber;
    // 上下文窗口最大容量（来自 ModelCatalog）
    private int _maxContextTokens;
    // 最近一轮 API 调用的 input tokens（≈ 当前上下文窗口实际占用）
    private int _lastRoundInputTokens;

    public int ExitCode { get; private set; }

    /// <summary>Exposes the chat input region to runtime wiring.</summary>
    internal ChatInputView ChatInput => _shell.ChatInput;

    /// <summary>
    /// Exposes the conversation transcript for external wiring.
    /// </summary>
    internal ChatTranscriptView Transcript => _shell.Transcript;

    public OneCodeToplevel(TuiContext ctx, IApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _ctx = ctx;
        _app = app;
        TabStop = TabBehavior.TabGroup;

        BorderStyle = LineStyle.None;
        SetScheme(TuiTheme.Base);

        // Resolve nullable TuiContext fields to defaults once, at the Toplevel level.
        // ReplShell/ChatInputView receive non-null references and skip their own fallback logic.
        var modeController = ctx.ModeController ?? new WorkingModeController();
        var keyResolver = ctx.KeyResolver ?? new KeybindingResolver();
        keyResolver.SetBindings([.. KeybindingDefaults.GetDefaultParsedBindings()]);
        var keyContextManager = ctx.KeyContextManager ?? new KeybindingContextManager();
        var toolNameProvider = ctx.GetToolNames ?? (() => []);

        _shell = new ReplShell(app, ctx.Version, ctx.Model, ctx.SshHost, ctx.SlashCommands,
            modeController, keyResolver, keyContextManager, ctx.Clipboard, ctx.GetSessionUserPrompts,
            toolNameProvider, gitHelper: ctx.GitHelper);
        _transcriptPresenter = new TranscriptEventPresenter(_shell.Transcript);
        _shell.ChatInput.Submitted += OnUserSubmitted;
        _shell.ChatInput.QuitRequested += OnQuitRequested;
        _shell.ChatInput.ImagePasteRequested += OnImagePasteRequested;
        _shell.ChatInput.InterruptRequested += OnInterruptRequested;

        // Wire multimodal support check and notifier for image paste rejection.
        _shell.ChatInput.IsMultimodalSupported = () => _ctx.ModelCatalog.SupportsAttachment(_ctx.Model);
        _shell.ChatInput.ImagePipeline = _ctx.ImagePipeline;
        _shell.ChatInput.ImagePasteRejected += () =>
        {
            Invoke(() => _shell.Transcript.AddError(
                $"The current model ({_ctx.Model}) does not support image attachments. " +
                "Switch to a multimodal model (e.g., claude-sonnet-4) to use images."));
        };

        // Ctrl+Shift+T — TEAM 模式下循环切换已注册团队
        _shell.ChatInput.CycleTeamRequested += OnCycleTeamRequested;

        _maxContextTokens = ModelContextDefaults.Resolve(ctx.Model, ctx.ModelCatalog);
        _shell.SessionContextBar.SetContextUsage(_maxContextTokens, 0);

        Add(_shell);

        // Ctrl+V smart paste is handled by ChatInputView.OnInputKeyPress directly.
        // ChatInputView handles Tab mode cycling through its WorkingModeController bridge.

        WireBackendCallbacks();

        RefreshTeamDisplay();
        // 模式切换时刷新团队标签可见性（离开 TEAM 模式时自动隐藏）
        modeController.ModeChanged += (_, _) => RefreshTeamDisplay();

        // Subscribe to LSP diagnostics changes so the status bar indicator
        // refreshes when servers publish new diagnostics. The event fires on
        // the LSP notification thread, so we marshal to the UI thread.
        if (_ctx.SubscribeDiagnosticsChanged is { } subscribe)
            subscribe(OnLspDiagnosticsChanged);
    }

    /// <summary>
    /// Unsubscribes LSP diagnostics listener on disposal to prevent leaks.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_ctx.UnsubscribeDiagnosticsChanged is { } unsubscribe)
                unsubscribe(OnLspDiagnosticsChanged);
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Handler for LspDiagnosticRegistry.DiagnosticsChanged — fires on the LSP
    /// notification thread. Marshals to the UI thread and dispatches a
    /// <see cref="TuiLspDiagnosticsChanged"/> event so the status bar refreshes.
    /// </summary>
    private void OnLspDiagnosticsChanged()
    {
        _app.Invoke(() => DispatchEvent(new TuiLspDiagnosticsChanged()));
    }

    /// <summary>
    /// Hooks the backend-supplied callbacks (in <see cref="TuiContext"/>) into
    /// the shell.
    /// </summary>
    private void WireBackendCallbacks()
    {
        // UI → Plan: forward a/r/s decisions from the shell back to the
        // InteractiveModeExecutor so it can approve/reject/edit the plan.
        _shell.PlanDecisionMade += decision =>
        {
            PlanDecisionReceived?.Invoke(decision);
        };
    }

    /// <summary>
    /// 刷新 AgentStatusBar 上的团队名显示。仅在 TEAM 模式下显示当前活跃团队；
    /// 其他模式下传 null 隐藏团队标签。
    /// </summary>
    private void RefreshTeamDisplay()
    {
        var inTeamMode = (_ctx.ModeController?.Mode ?? WorkingMode.Build) == WorkingMode.Team;
        var teamName = inTeamMode ? _ctx.GetActiveTeam?.Invoke() : null;
        var resolvedMode = inTeamMode && teamName is not null
            ? _ctx.GetTeamModeLabel?.Invoke(teamName)
            : null;
        _shell.AgentStatusBar.SetActiveTeam(teamName, resolvedMode);
    }

    /// <summary>
    /// Ctrl+Shift+T 事件处理：调用 TuiContext.CycleTeam 切换到下一个已注册团队，
    /// 然后刷新 AgentStatusBar 显示并给出系统提示。
    /// </summary>
    private void OnCycleTeamRequested()
    {
        if ((_ctx.ModeController?.Mode ?? WorkingMode.Build) != WorkingMode.Team)
            return;

        var newTeam = _ctx.CycleTeam?.Invoke();
        if (string.IsNullOrEmpty(newTeam))
        {
            _shell.Transcript.AddSystem("没有已注册的团队可切换。使用 /team list 查看可用团队。");
            return;
        }

        RefreshTeamDisplay();
        _shell.Transcript.AddSystem($"已切换到团队: {newTeam}");
    }

    /// <summary>
    /// Internally invoked when the backend reports a new plan. Dispatches onto
    /// the UI thread because <see cref="Tools.CreatePlanTool"/> runs on a background
    /// task thread.
    /// </summary>
    internal void ShowPlanFromBackend(
        string title,
        IReadOnlyList<PlanStep> steps,
        PlanCardPhase phase,
        string? markdown = null,
        string? documentPath = null)
    {
        _app.Invoke(() =>
        {
            _shell.ShowPlanCard(title, steps, phase, markdown, documentPath);
        });
    }

    /// <summary>
    /// Raised when the user approves / rejects / edits the plan card via
    /// keyboard. Consumed by the host to drive the plan-mode workflow.
    /// </summary>
    internal event Action<PlanCardDecision>? PlanDecisionReceived;

    internal void ScheduleInitialFocus()
    {
        _app.AddTimeout(TimeSpan.Zero, () =>
        {
            _shell.Transcript.ShowWelcome(new WelcomeInfo(_ctx.Version));
            _shell.FocusChatInput();

            // Refresh the session context bar (git branch/worktree) once on startup.
            _ = _shell.RefreshSessionContextBarAsync();
            RefreshSessionName();

            // Post-TUI trust check: if the pre-TUI startup path silently accepted
            // trust (because IApplication wasn't ready yet), re-prompt now that
            // Terminal.Gui is fully initialized so the real TrustOverlay can be shown.
            // 初始 prompt 注入必须在 trust 确认之后，否则 trust 被拒绝时 prompt
            // 仍会开始执行，与 RequestStop 竞态。
            if (_ctx.TrustService is { NeedsPostTuiConfirmation: true } trustService)
            {
                _ = ShowPostTuiTrustAsync(trustService, SubmitInitialPrompt);
            }
            else
            {
                SubmitInitialPrompt();
            }

            return false;
        });
    }

    /// <summary>
    /// 如果 CLI 传入了位置参数 prompt（存放在 TuiContext.InitialPrompt），
    /// 在 TUI 启动后把它当作用户首次提交的输入注入，走正常的 OnUserSubmitted 流程。
    /// 必须在 workspace trust 确认通过后调用。
    /// </summary>
    private void SubmitInitialPrompt() =>
        _ = ReplayBuildRunThenSubmitInitialPromptAsync(_ctx.ExternalCancellation);

    private async Task ReplayBuildRunThenSubmitInitialPromptAsync(CancellationToken ct)
    {
        // Persisted BuildRun state must be restored before a new prompt can emit
        // newer live events; otherwise a slow replay may append stale state after
        // the new run has already started.
        await ReplayCurrentBuildRunAsync(ct).ConfigureAwait(false);

        var prompt = _ctx.InitialPrompt;
        if (string.IsNullOrWhiteSpace(prompt) || ct.IsCancellationRequested)
            return;

        // OnUserSubmitted mutates Terminal.Gui controls and must run on the UI loop.
        _app.Invoke(() => OnUserSubmitted(prompt));
    }

    internal async Task ReplayCurrentBuildRunAsync(CancellationToken ct = default)
    {
        if (_ctx.ReplayCurrentBuildRun is not { } replay)
            return;

        try
        {
            var state = await replay(ct).ConfigureAwait(false);
            if (state is not null)
                _app.Invoke(() => DispatchEvent(state));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _app.Invoke(() => _shell.Transcript.AddError(
                $"Failed to replay BuildRun state: {ex.Message}"));
        }
    }

    /// <summary>同步 SessionContextBar 上的会话名（Name 优先，否则短 Id）。</summary>
    private void RefreshSessionName()
    {
        var name = _ctx.GetSessionName?.Invoke();
        _shell.SessionContextBar.SetSessionName(name);
    }

    private async Task ShowPostTuiTrustAsync(TrustService trustService, Action? onAccepted = null)
    {
        // Reset the session-level accept so EnsureTrustAsync re-evaluates against
        // the persisted trust list. The directory was not persisted in the pre-TUI
        // path, only accepted for the session, so the overlay will now be shown
        // via the TUI modal path.
        trustService.ResetSessionTrustForPostTuiCheck();
        var accepted = await trustService.EnsureTrustAsync(_ctx.ExternalCancellation).ConfigureAwait(false);
        if (!accepted)
        {
            _app.Invoke(() =>
            {
                _shell.Transcript.AddSystem("Workspace trust was declined. The application will close.");
                _app.RequestStop();
            });
            return;
        }

        // Trust 确认通过后才注入初始 prompt，避免与 RequestStop 竞态。
        onAccepted?.Invoke();
    }

    // Event handlers & submit dispatch live in OneCodeToplevel.Dispatch.cs
    // Session/config modal handlers live in OneCodeToplevel.Modals.cs

    /// <summary>
    /// Push any view onto the overlay host on the UI thread.
    /// Used by delegates that display overlays (e.g. settings, resume chooser).
    /// </summary>
    public void PushOverlay(View overlay)
    {
        _app.Invoke(() =>
        {
            _shell.Overlays.Visible = true;
            _shell.Overlays.Push(overlay);
        });
    }

    /// <summary>
    /// Pop the topmost overlay from the host on the UI thread and restore focus.
    /// </summary>
    public void PopTopOverlay()
    {
        _app.Invoke(() =>
        {
            _shell.Overlays.Pop();
            _shell.Overlays.Visible = _shell.Overlays.IsOverlayVisible;
        });
    }

    public void LoadConversation(Conversation conversation)
    {
        _transcriptPresenter.Reset();
        _shell.Transcript.LoadConversation(conversation);
        _shell.AgentStatusBar.SetModel(conversation.Model);

        // 重置 token/turn/tool 计数器，避免显示前一会话的累积数据
        _inputTokens = 0;
        _outputTokens = 0;
        _cacheReadTokens = 0;
        _cacheWriteTokens = 0;
        _turnNumber = 0;
        _shell.SessionContextBar.SetTokens(0, 0);
        _shell.SessionContextBar.SetTurn(0);

        _maxContextTokens = ModelContextDefaults.Resolve(conversation.Model);
        _shell.SessionContextBar.SetContextUsage(_maxContextTokens, 0);

        _shell.ClearPlan();

        _shell.FocusChatInput();
    }

    public void UpdateRuntimeState(string? model = null, string? effort = null)
    {
        if (model != null)
        {
            _shell.AgentStatusBar.SetModel(model);
            _shell.UpdateHeader(model);

            // 模型变更时重新解析上下文窗口
            _maxContextTokens = ModelContextDefaults.Resolve(model);
            _shell.SessionContextBar.SetContextUsage(_maxContextTokens, _lastRoundInputTokens);

        }
        _shell.FocusChatInput();
    }

    // Query loop and event dispatch live in OneCodeToplevel.Query.cs and OneCodeToplevel.Events.cs

    private void Invoke(Action action) => _app.Invoke(action);
}
