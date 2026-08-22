using System.Text;
using OneCode.App.Commands;
using OneCode.App.Tui;
using OneCode.Core.PlanMode;
using OneCode.Infrastructure.Config;
using OneCode.Infrastructure.Mcp;

namespace OneCode.App.Services;

/// <summary>
/// Encapsulates the <see cref="TuiHost.Run"/> factory and all TUI overlay
/// routing that previously lived inline in
/// <see cref="InteractiveModeExecutor.ExecuteAsync"/>.
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
    ILogger<TuiHostConfigurator> logger,
    TuiOverlayDependencies overlay,
    TuiCommandSurfaceDependencies commandSurface)
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

            // 绑定 EmitEvent 到 OneCodeToplevel.DispatchEvent
            // emitEventBinder 是一个委托，它接收一个 Action<TuiEvent> 并将其存储
            // 这里我们传入 toplevel.DispatchEvent 方法，让 UserQuestionService 可以通过 EmitEvent 调用它
            emitEventBinder(toplevel.DispatchEvent);

            // Dynamic command refresh callback
            // Pushed into ChatInputView whenever skills or MCP servers change.
            Action<IReadOnlyList<SlashCommandEntry>>? updateCommandsUi = null;
            updateCommandsUi = commands => app.Invoke(() => toplevel.ChatInput.UpdateCommands(commands));

            // 工作模式切换时刷新斜杠命令列表，使依赖 ICommand.IsEnabled() 动态启用的命令
            // （如仅在 BUILD 模式可用的 /permissions）能实时出现/消失。
            session.ModeController.ModeChanged += (_, _) => updateCommandsUi(
                commandSurface.CommandRegistry.GetAll()
                    .Select(c => new SlashCommandEntry(
                        c.Name, c.Description, c.Source, c.ArgumentHint))
                    .ToList());

            // Skill file change hot-reload
            async void OnSkillsChanged()
            {
                var skillSource = commandSurface.DynamicCommandSources.OfType<SkillCommandSource>().FirstOrDefault();
                if (skillSource is null) return;
                try
                {
                    await commandSurface.CommandRegistry.RefreshDynamicCommandsAsync([skillSource], ct).ConfigureAwait(false);
                    var updated = commandSurface.CommandRegistry.GetAll()
                        .Select(c => new SlashCommandEntry(
                            c.Name,
                            c.Description,
                            c.Source,
                            c.ArgumentHint))
                        .ToList();
                    updateCommandsUi?.Invoke(updated);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to refresh skill commands after file change");
                }
            }

            // MCP server connection change hot-reload
            async void OnMcpServersChanged()
            {
                var mcpSource = commandSurface.DynamicCommandSources.OfType<McpCommandSource>().FirstOrDefault();
                if (mcpSource is null) return;
                try
                {
                    await commandSurface.CommandRegistry.RefreshDynamicCommandsAsync([mcpSource], ct).ConfigureAwait(false);
                    var updated = commandSurface.CommandRegistry.GetAll()
                        .Select(c => new SlashCommandEntry(
                            c.Name,
                            c.Description,
                            c.Source,
                            c.ArgumentHint))
                        .ToList();
                    updateCommandsUi?.Invoke(updated);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to refresh MCP commands after server connection change");
                }
            }

            overlay.SkillChangeWatcher.SkillsChanged += OnSkillsChanged;
            mcpConnectionManager.ServersChanged += OnMcpServersChanged;

            overlay.PlanExecutionRecovery.AttachSession(session);
            WirePlanCard(toplevel, session, app);
            ctx.TrustService?.SetOverlayDelegates(toplevel.PushOverlay, toplevel.PopTopOverlay);
            WireSessionModals(toplevel, session, app);
            WireSessionRefreshCallback(toplevel, session, cmdState, app);
            WireSettingsModal(toplevel, app);
            WireStartupHints(toplevel, app);

            return toplevel;
        });

        return exitCode;
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

    /// <summary>
    /// Projects persisted workflow state to the plan card. The TUI only emits
    /// typed approval commands; it never mutates workflow state or starts an agent run itself.
    /// </summary>
    private void WirePlanCard(OneCodeToplevel toplevel, InteractiveSession session, IApplication app)
    {
        overlay.PlanCardPublisher.PlanCreated += (title, steps, phase) =>
            toplevel.ShowPlanFromBackend(title, steps, phase);

        overlay.PlanCardPublisher.WorkflowChanged += workflow =>
            _ = ProjectAndShowPlanAsync(workflow, toplevel, app);


        toplevel.PlanDecisionReceived += decision =>
            _ = HandlePlanDecisionAsync(decision, session, toplevel, app);
    }

    private async Task ProjectAndShowPlanAsync(
        PlanWorkflow workflow,
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
            var steps = ProjectSteps(workflow, definitions);
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
            var workflow = await overlay.PlanWorkflow.GetAsync(conversation.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("No active plan workflow exists for this session.");
            if (workflow.State != PlanWorkflowState.AwaitingApproval
                || workflow.SubmittedRevision is not { } revision)
            {
                throw new InvalidOperationException(
                    $"Plan approval is not available in state '{workflow.State}'.");
            }

            var commandId = Guid.NewGuid().ToString("N");
            switch (decision)
            {
                case PlanCardDecision.Approve:
                    {
                        var result = await overlay.PlanWorkflow.ApproveAsync(new ApprovePlanCommand(
                            commandId,
                            conversation.Id,
                            workflow.Id,
                            revision,
                            workflow.Version,
                            "interactive-user")).ConfigureAwait(false);
                        overlay.PlanCardPublisher.Publish(result.Workflow);
                        await overlay.PlanRunDispatcher.StartBuildAsync(session, result.Workflow).ConfigureAwait(false);
                        break;
                    }
                case PlanCardDecision.Reject:
                    {
                        var result = await overlay.PlanWorkflow.RejectAsync(new RejectPlanCommand(
                            commandId,
                            conversation.Id,
                            workflow.Id,
                            revision,
                            workflow.Version,
                            "Rejected by interactive user.")).ConfigureAwait(false);
                        overlay.PlanCardPublisher.Publish(result.Workflow);
                        break;
                    }
                case PlanCardDecision.Edit:
                    {
                        var result = await overlay.PlanWorkflow.RequestEditAsync(new RequestPlanEditCommand(
                            commandId,
                            conversation.Id,
                            workflow.Id,
                            revision,
                            workflow.Version,
                            "请根据用户反馈修订计划。",
                            [])).ConfigureAwait(false);
                        overlay.PlanCardPublisher.Publish(result.Workflow);
                        break;
                    }
            }
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
        PlanWorkflowState.Verifying => PlanCardPhase.Verifying,
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

    private static IReadOnlyList<PlanStep> ProjectSteps(
        PlanWorkflow workflow,
        IReadOnlyList<PlanStepDefinition>? definitions)
    {
        if (definitions is not { Count: > 0 })
            return [new PlanStep("正在整理计划步骤", null, null, PlanStepStatus.Current)];

        var executions = workflow.StepExecutions.ToDictionary(step => step.StepId, StringComparer.Ordinal);
        return definitions.Select(step =>
        {
            var status = executions.TryGetValue(step.Id, out var execution)
                ? execution.Status switch
                {
                    PlanStepExecutionStatus.InProgress => PlanStepStatus.Current,
                    PlanStepExecutionStatus.Completed or PlanStepExecutionStatus.Skipped => PlanStepStatus.Done,
                    _ => PlanStepStatus.Pending,
                }
                : PlanStepStatus.Pending;
            return new PlanStep(step.Title, step.Description, null, status);
        }).ToArray();
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
                if (conversation == null)
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
                return;

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
                var currentAppState = appStateAccessor.Current;
                var snapshot = configManager.Current;
                var settings = snapshot.Effective;

                var currentModelId = currentAppState.MainLoopModel
                    ?? settings.Model
                    ?? string.Empty;
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
