using OneCode.Core.Mcp;
using OneCode.Core.Config;
using System.Text;
using OneCode.App.Commands;
using OneCode.App.Tui;
using OneCode.Core.Coordinator;
using OneCode.Core.PlanMode;
using OneCode.App.Services.PlanMode;

namespace OneCode.App.Services;

/// <summary>
/// Encapsulates the <see cref="TuiHost.Run"/> factory and all TUI overlay
/// routing (Plan card 审批、overlay 委托、动态命令刷新等) orthogonal to query streaming.
/// </summary>
/// <remarks>
/// Owns five overlay concerns that are orthogonal to query streaming:
/// <list type="bullet">
/// <item>Plan card publishing + decision routing (Approve/Reject/Edit)</item>
/// <item>TrustService overlay delegate registration</item>
/// <item>Session resume/switch modals</item>
/// <item>Settings overlay + apply pipeline</item>
/// <item>Dynamic command refresh on Skill/MCP changes</item>
/// </list>
/// </remarks>
public sealed class TuiHostConfigurator(
    IConfigManager configManager,
    IAppStateAccessor appStateAccessor,
    IMcpConnectionManager mcpConnectionManager,
    Services.Mcp.McpConfigService mcpConfigService,
    ILogger<TuiHostConfigurator> logger,
    TuiOverlayDependencies overlay,
    TuiCommandSurfaceDependencies commandSurface,
    IPermissionModeProvider permissionModes)
{
    /// <summary>
    /// Runs the TUI host with full overlay wiring. Blocks until the user quits.
    /// Returns the process exit code.
    /// </summary>
    public int Run(
        TuiContext ctx,
        InteractiveSession session,
        CommandExecutionState cmdState,
        Action<Action<TuiEvent>> emitEventBinder,
        CancellationToken ct)
    {
        var exitCode = TuiHost.Run(app =>
        {
            var toplevel = new OneCodeToplevel(ctx, app);

            // 绑定 EmitEvent 到 OneCodeToplevel.DispatchEvent。
            // EmitEvent 的调用方（Plan 派发器、UserQuestionService 等）运行在后台线程，
            // 而 DispatchEvent 直接触碰 Terminal.Gui 视图树，必须经 app.Invoke 编组回 UI 线程，
            // 否则 Terminal.Gui 抛出 "Call from invalid thread."。
            // TUI 退出后 run 收尾线程仍可能发射事件（如 TuiWorkflowRunEnded），此时吞掉编组
            // 异常并记日志，避免 catch/finally 中的二次抛出掩盖原始错误。
            emitEventBinder(evt =>
            {
                try
                {
                    app.Invoke(() => toplevel.DispatchEvent(evt));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Dropped TUI event {EventType} because the TUI host is no longer running", evt.GetType().Name);
                }
            });

            // Dynamic command refresh callback
            // Pushed into ChatInputView whenever skills or MCP servers change.
            Action<IReadOnlyList<SlashCommandEntry>>? updateCommandsUi = null;
            updateCommandsUi = commands => app.Invoke(() => toplevel.ChatInput.UpdateCommands(commands));

            // 工作模式切换时刷新斜杠命令列表，使依赖 ICommand.IsEnabled() 动态启用的命令
            // （如仅在 BUILD 模式可用的 /permissions）能实时出现/消失。
            session.ModeController.ModeChanged += (_, _) => updateCommandsUi(
                ProjectSlashCommands(commandSurface.CommandRegistry));

            // 动态命令源热重载（skill 文件变更 / MCP 服务器连接变化）：刷新命中的源并重发命令列表。
            async void OnDynamicCommandsChanged<TSource>(string sourceKind)
                where TSource : IDynamicCommandSource
            {
                var source = commandSurface.DynamicCommandSources.OfType<TSource>().FirstOrDefault();
                if (source is null) return;
                try
                {
                    await commandSurface.CommandRegistry.RefreshDynamicCommandsAsync([source], ct).ConfigureAwait(false);
                    updateCommandsUi?.Invoke(ProjectSlashCommands(commandSurface.CommandRegistry));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to refresh {SourceKind} commands after change", sourceKind);
                }
            }

            overlay.SkillFilesWatcher.SkillsChanged += () => OnDynamicCommandsChanged<SkillCommandSource>("skill");
            mcpConnectionManager.ServersChanged += () => OnDynamicCommandsChanged<McpCommandSource>("MCP");

            overlay.PlanExecutionRecovery.AttachSession(session);
            WirePlanCard(toplevel, session, app);
            WireTodoPanel(toplevel, app);
            ctx.TrustService?.SetOverlayDelegates(toplevel.PushOverlay, toplevel.PopTopOverlay);
            WireSessionModals(toplevel, session, app);
            WireSessionRefreshCallback(toplevel, session, cmdState, app);
            WireSettingsModal(toplevel, app);
            WireMcpConfigModal(toplevel);
            WireSessionPermissionEscalation(toplevel);
            WireStartupHints(toplevel, app);

            return toplevel;
        });

        return exitCode;
    }

    /// <summary>
    /// Subscribes the todo-strip projection: <c>TodoProjectionService</c> publishes
    /// Harness <c>TodoProvider</c> session snapshots onto the unified
    /// <c>OrchestrationEventBus</c> at run start, whenever a todos_* mutation tool completes,
    /// and at run end. Same marshalling
    /// contract as the plan card (handler runs on the emitting thread;
    /// <see cref="OneCodeToplevel.ShowTodos"/> moves rendering to the UI thread).
    /// An empty snapshot hides the strip.
    /// </summary>
    private void WireTodoPanel(OneCodeToplevel toplevel, IApplication app)
    {
        _ = overlay.OrchestrationEvents.Subscribe(evt =>
        {
            if (evt is not OrchestrationEvent.TodoProjectionChanged { Items: var items })
                return;
            app.Invoke(() => toplevel.ShowTodos(items));
        });
    }

    /// <summary>
    /// Wires <see cref="IStartupHintCollector"/> so that actionable hints produced by
    /// background services (e.g. "Go project detected but gopls not installed") are
    /// rendered in the conversation transcript. Hints that arrived before the TUI was
    /// ready are flushed on wiring; subsequent hints are delivered live via the
    /// <see cref="IStartupHintCollector.HintAdded"/> event. All rendering is marshalled
    /// to the Terminal.Gui main loop via <c>IApplication.Invoke</c>.
    /// </summary>
    private void WireStartupHints(OneCodeToplevel toplevel, IApplication app)
    {
        // Flush hints that arrived before the TUI was ready (e.g. LSP detection that
        // completed between ApplicationStarted and TuiHost.Run). These are shown
        // immediately so the user sees them on the welcome screen.
        foreach (var hint in commandSurface.StartupHintCollector.GetPending())
        {
            var captured = hint;
            app.Invoke(() => toplevel.Transcript.AddSystem(captured.Message));
        }

        // Live subscription: hints arriving after this point (e.g. LSP detection still
        // running in the background) are pushed to the transcript as they come in.
        commandSurface.StartupHintCollector.HintAdded += hint =>
        {
            var captured = hint;
            app.Invoke(() => toplevel.Transcript.AddSystem(captured.Message));
        };
    }

    /// <summary>将命令注册表投影为 TUI 输入框的斜杠命令条目。</summary>
    private static List<SlashCommandEntry> ProjectSlashCommands(ICommandRegistry registry) =>
        registry.GetAll()
            .Select(c => new SlashCommandEntry(c.Name, c.Description, c.Source, c.ArgumentHint))
            .ToList();

    /// <summary>
    /// Subscribes the unified OrchestrationEvent bus: the plan
    /// projection event is asynchronously resolved (revision store lookup) and rendered;
    /// the event is captured on the emitting thread, and the UI thread is marshaled
    /// by <see cref="ProjectAndShowPlanAsync"/> via <c>app.Invoke</c>.
    /// </summary>
    private void WirePlanCard(OneCodeToplevel toplevel, InteractiveSession session, IApplication app)
    {
        // 步骤进度的唯一事实源是 agent 待办清单：待办投影一变化就用最近一次工作流重绘卡片。
        PlanWorkflow? shownWorkflow = null;
        IReadOnlyList<TodoListItem> todos = [];
        overlay.OrchestrationEvents.Subscribe(evt =>
        {
            switch (evt)
            {
                case OrchestrationEvent.PlanProjectionChanged { Workflow: var workflow }:
                    shownWorkflow = workflow;
                    _ = ProjectAndShowPlanAsync(workflow, todos, toplevel, app);
                    break;
                case OrchestrationEvent.TodoProjectionChanged { Items: var items }:
                    todos = items;
                    if (shownWorkflow is { State: PlanWorkflowState.Executing } executing)
                        _ = ProjectAndShowPlanAsync(executing, todos, toplevel, app);
                    break;
            }
        });

        toplevel.PlanDecisionReceived += decision =>
            _ = HandlePlanDecisionAsync(decision, session, toplevel, app);
    }

    private async Task ProjectAndShowPlanAsync(
        PlanWorkflow workflow,
        IReadOnlyList<TodoListItem> todos,
        OneCodeToplevel toplevel,
        IApplication app)
    {
        var phase = MapPlanCardPhase(workflow.State);
        if (phase is null)
            return;

        try
        {
            var revision = await ResolveDisplayedRevisionAsync(workflow).ConfigureAwait(false);
            var markdown = revision?.Markdown ?? workflow.ApprovedSnapshot?.Markdown;
            var definitions = revision?.Steps ?? workflow.ApprovedSnapshot?.Steps;
            var steps = PlanStepProjector.Project(workflow, definitions, todos);
            var title = revision?.Title
                ?? (markdown is not null ? ExtractPlanTitle(markdown) : "实施计划");
            // 计划 markdown 投影的持久化路径——显示在卡片上让用户能找到 plan 文档。
            var displayedRevision = revision?.Revision ?? workflow.ApprovedSnapshot?.Revision;
            var documentPath = displayedRevision is { } rev
                ? overlay.PlanAggregateStore.GetRevisionMarkdownPath(workflow.SessionId, workflow.Id, rev)
                : null;

            app.Invoke(() => toplevel.ShowPlanFromBackend(
                title,
                steps,
                phase.Value,
                // 审批阶段渲染完整计划全文供用户审阅；
                // 执行阶段不渲染全文（步骤列表 + 文档路径保持精简）。
                phase is PlanCardPhase.PendingApproval ? markdown : null,
                documentPath));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to project plan workflow {PlanId}", workflow.Id);
            app.Invoke(() => toplevel.Transcript.AddError($"Plan display failed: {ex.Message}"));
        }
    }

    private async Task<PlanRevision?> ResolveDisplayedRevisionAsync(PlanWorkflow workflow)
    {
        var revision = workflow.SubmittedRevision ?? workflow.LatestRevision;
        if (revision <= 0)
            return null;

        var aggregate = await overlay.PlanAggregateStore
            .LoadAsync(workflow.SessionId)
            .ConfigureAwait(false);
        return aggregate?.FindRevision(revision);
    }

    private async Task HandlePlanDecisionAsync(
        PlanCardDecision decision,
        InteractiveSession session,
        OneCodeToplevel toplevel,
        IApplication app)
    {
        var conversation = session.SessionManager.ForegroundConversation;
        if (conversation is null)
            return;

        try
        {
            // 决策编排（状态校验/命令构造/投影发布/批准后 Build 派发）已收敛至
            // AggregateApprovalGate（控制面并入）；此处仅保留 TUI 错误呈现。
            await overlay.PlanApprovalGate.DecideAsync(session, decision).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process plan decision {Decision}", decision);
            app.Invoke(() => toplevel.Transcript.AddError($"Plan decision failed: {ex.Message}"));
        }
    }

    private static PlanCardPhase? MapPlanCardPhase(PlanWorkflowState state) => state switch
    {
        // 无已提交计划时不展示卡片（Planning 仅存在于提交之前的初始态）。
        PlanWorkflowState.Planning => null,
        PlanWorkflowState.FinalizingPlanRun => PlanCardPhase.Finalizing,
        PlanWorkflowState.AwaitingApproval => PlanCardPhase.PendingApproval,
        PlanWorkflowState.StartingExecution => PlanCardPhase.StartingExecution,
        PlanWorkflowState.Executing => PlanCardPhase.Executing,
        PlanWorkflowState.Completed => PlanCardPhase.Completed,
        PlanWorkflowState.Failed => PlanCardPhase.Failed,
        PlanWorkflowState.Cancelled => PlanCardPhase.Cancelled,
        _ => null,
    };

    private static string ExtractPlanTitle(string markdown)
    {
        var title = markdown
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith("#", StringComparison.Ordinal))?
            .TrimStart('#', ' ');
        return string.IsNullOrWhiteSpace(title) ? "实施计划" : title;
    }

    /// <summary>
    /// Configures session resume/switch modals on the toplevel.
    /// </summary>
    private void WireSessionModals(OneCodeToplevel toplevel, InteractiveSession session, IApplication app)
    {
        toplevel.ConfigureSessionModals(
            showResumeChooserAsync: token => OverlayLaunchers.ShowResumeChooserAsync(
                toplevel.PushOverlay, toplevel.PopTopOverlay, session.SessionManager, token),
            resumeSessionAsync: async (sessionId, token) =>
            {
                var conversation = await session.SessionManager.SwitchToSessionAsync(sessionId, token)
                    .ConfigureAwait(false);
                if (conversation is null)
                {
                    throw new InvalidOperationException($"Session '{sessionId}' not found.");
                }

                appStateAccessor.Update(s => s with { MainLoopModel = conversation.Model });

                var workflow = await overlay.PlanWorkflow.GetAsync(conversation.Id, token)
                    .ConfigureAwait(false);
                app.Invoke(() => toplevel.LoadConversation(conversation));
                await toplevel.ReplayCurrentBuildRunAsync(token).ConfigureAwait(false);
                if (workflow is not null)
                {
                    // PlanCardPublisher 已是统一总线发射器，重放订阅链路与实时同构。
                    overlay.PlanCardPublisher.Publish(workflow);
                    if (workflow.State == PlanWorkflowState.StartingExecution
                        && (workflow.NextRetryAt is null || workflow.NextRetryAt <= DateTimeOffset.UtcNow))
                    {
                        await overlay.PlanRunDispatcher.StartBuildAsync(session, workflow, token)
                            .ConfigureAwait(false);
                    }
                }
            });
    }

    /// <summary>
    /// Sets the session-UI refresh callback on <paramref name="cmdState"/>,
    /// invoked after session-affecting commands (e.g. /session).
    /// </summary>
    private static void WireSessionRefreshCallback(
        OneCodeToplevel toplevel,
        InteractiveSession session,
        CommandExecutionState cmdState,
        IApplication app)
    {
        cmdState.SetRefreshUiCallback(async ct =>
        {
            var conversation = session.SessionManager.ForegroundConversation;
            if (conversation is null)
            {
                // /close closed the foreground session — clear the transcript so
                // stale messages don't mix into the lazily-created next session.
                app.Invoke(() => toplevel.ClearConversationView());
                return;
            }

            app.Invoke(() => toplevel.LoadConversation(conversation));
            await toplevel.ReplayCurrentBuildRunAsync(ct).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Configures the settings overlay: show returns user-selected settings,
    /// apply persists them to config and updates AppState + TUI runtime state.
    /// </summary>
    private void WireSettingsModal(
        OneCodeToplevel toplevel,
        IApplication app)
    {
        SettingsResult? pendingSettings = null;
        toplevel.ConfigureSettingsModal(
            showSettingsOverlayAsync: async token =>
            {
                var snapshot = configManager.Current;
                pendingSettings = await OverlayLaunchers.ShowSettingsOverlayAsync(
                    toplevel.PushOverlay,
                    toplevel.PopTopOverlay,
                    snapshot,
                    projectScopeAvailable: configManager.ProjectSettingsFilePath is not null,
                    ct: token).ConfigureAwait(false);

                return pendingSettings is not null;
            },
            applySettingsAsync: async token =>
            {
                if (pendingSettings is null)
                    throw new InvalidOperationException("No pending settings result is available.");

                var settingsResult = pendingSettings;
                var changes = BuildSettingsPatch(settingsResult);
                var applyResult = await configManager.ApplyAsync(
                    new ConfigPatch(settingsResult.TargetScope, changes),
                    token).ConfigureAwait(false);
                if (!applyResult.Saved)
                    throw new IOException(applyResult.Error ?? "配置保存失败。");

                var effective = applyResult.Snapshot.Effective;
                appStateAccessor.Update(state => state with
                {
                    MainLoopModel = effective.Model,
                    ThinkingEnabled = effective.Get("thinkingEnabled", false),
                    ShowThinking = effective.Get("showThinking", false),
                    EffortValue = EffortThinking.ParseEffort(effective.Get("effortValue", "medium") ?? "medium"),
                });

                app.Invoke(() => toplevel.UpdateRuntimeState(
                    model: effective.Model ?? settingsResult.Model,
                    effort: effective.Get("effortValue", "medium") ?? "medium"));

                pendingSettings = null;
                var summary = FormatSettingsApplyResult(settingsResult.TargetScope, applyResult);
                logger.LogInformation(
                    "Settings applied to {Scope}: {Summary}",
                    settingsResult.TargetScope,
                    summary);
                return summary;
            });
    }

    /// <summary>
    /// /mcp（无参）在 TUI 中打开 MCP 工具白名单配置页：
    /// Prepare 拉取服务器快照 → overlay 勾选 → Apply 写回配置并热生效，
    /// 保存摘要作为命令结果渲染到会话记录（与 /config 模式一致）。
    /// </summary>
    private void WireMcpConfigModal(OneCodeToplevel toplevel)
    {
        toplevel.ConfigureMcpConfigModal(async token =>
        {
            var entries = await mcpConfigService.PrepareAsync(token).ConfigureAwait(false);

            var result = await OverlayLaunchers.ShowMcpConfigOverlayAsync(
                toplevel.PushOverlay,
                toplevel.PopTopOverlay,
                entries,
                token).ConfigureAwait(false);
            if (result is null || result.Servers.Count == 0)
                return null;

            return await mcpConfigService.ApplyAsync(result, token).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// 会话级全放行接线：审批弹窗选择「本次对话全部允许」时，把 PermissionModeProvider
    /// 运行时覆盖切到 BypassPermissions（Permission 层 AllowAll；Layer 0 安全不变量不受影响）。
    /// 只设运行时覆盖、不写配置——重启后恢复持久档位。
    /// BypassPermissions 属于 WorkingModeBridge 的 CLI 高级档保护集，模式切换不会静默覆盖它。
    /// </summary>
    private void WireSessionPermissionEscalation(OneCodeToplevel toplevel)
    {
        toplevel.ConfigureSessionPermissionEscalation(() =>
        {
            permissionModes.SetCurrentMode(PermissionMode.BypassPermissions);
            logger.LogInformation(
                "Session-scoped allow-all enabled from the approval prompt (BypassPermissions runtime override, not persisted)");
        });
    }

    internal static IReadOnlyDictionary<string, ConfigMutation> BuildSettingsPatch(SettingsResult result)
    {
        var effective = result.InitialSnapshot.Effective;
        var changes = new Dictionary<string, ConfigMutation>(StringComparer.OrdinalIgnoreCase);

        AddIfChanged(changes, OneCode.Core.Constants.ConfigKeys.Provider, effective.Provider, result.Provider);
        AddIfChanged(changes, OneCode.Core.Constants.ConfigKeys.BaseUrl, effective.BaseUrl ?? string.Empty, result.BaseUrl);
        AddIfChanged(changes, OneCode.Core.Constants.ConfigKeys.Model, effective.Model ?? string.Empty, result.Model);
        AddIfChanged(
            changes,
            OneCode.Core.Constants.ConfigKeys.FastModel,
            effective.Get<string>(OneCode.Core.Constants.ConfigKeys.FastModel) ?? string.Empty,
            result.FastModel);
        AddIfChanged(changes, OneCode.Core.Constants.ConfigKeys.WebSearchProvider, effective.WebSearchProvider, result.WebSearchProvider);
        AddIfChanged(changes, OneCode.Core.Constants.ConfigKeys.NotificationsEnabled, effective.NotificationsEnabled, result.NotificationsEnabled);
        AddIfChanged(changes, OneCode.Core.Constants.ConfigKeys.MaxTurns, effective.MaxTurns, result.MaxTurns);
        AddIfChanged(changes, "thinkingEnabled", effective.Get("thinkingEnabled", false), result.ThinkingEnabled);
        AddIfChanged(changes, "showThinking", effective.Get("showThinking", false), result.ShowThinking);
        AddIfChanged(changes, "effortValue", effective.Get("effortValue", "medium") ?? "medium", result.Effort);

        if (result.ApiKeyChanged)
        {
            changes[OneCode.Core.Constants.ConfigKeys.ApiKey] = string.IsNullOrEmpty(result.ApiKey)
                ? new ConfigMutation.Remove()
                : new ConfigMutation.Set(result.ApiKey);
        }

        if (result.TavilyApiKeyChanged)
        {
            // 语义与 LLM ApiKeyChanged 一致：空值 = 移除当前作用域的密钥。
            changes[OneCode.Core.Constants.ConfigKeys.TavilyApiKey] = string.IsNullOrEmpty(result.TavilyApiKey)
                ? new ConfigMutation.Remove()
                : new ConfigMutation.Set(result.TavilyApiKey);
        }

        return changes;
    }

    private static void AddIfChanged<T>(
        IDictionary<string, ConfigMutation> changes,
        string key,
        T oldValue,
        T newValue)
    {
        if (!EqualityComparer<T>.Default.Equals(oldValue, newValue))
            changes[key] = new ConfigMutation.Set(newValue);
    }

    private static string FormatSettingsApplyResult(ConfigScope scope, ConfigApplyResult result)
    {
        var builder = new StringBuilder($"配置已保存到{(scope == ConfigScope.Project ? "项目级" : "用户级")}。");
        AppendCategory(builder, "立即生效", result.ImmediateChanges);
        AppendCategory(builder, "下次操作生效", result.NextOperationChanges);
        AppendCategory(builder, "重启后生效", result.RestartRequiredChanges);
        AppendCategory(builder, "被更高优先级覆盖", result.OverriddenChanges);
        if (result.ImmediateChanges.Count == 0
            && result.NextOperationChanges.Count == 0
            && result.RestartRequiredChanges.Count == 0)
        {
            builder.AppendLine().Append("  没有有效值变化。");
        }
        return builder.ToString();
    }

    private static void AppendCategory(StringBuilder builder, string title, IReadOnlyCollection<string> keys)
    {
        if (keys.Count > 0)
            builder.AppendLine().Append("  ").Append(title).Append("：").Append(string.Join("、", keys));
    }
}
