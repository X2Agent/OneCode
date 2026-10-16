using OneCode.Core.Lsp;

namespace OneCode.App.Tui;

/// <summary>
/// TuiEvent dispatch for <see cref="OneCodeToplevel"/>.
/// Hosts the <see cref="DispatchEvent"/> switch and its private helpers.
/// </summary>
public sealed partial class OneCodeToplevel
{
    /// <summary>
    /// Dispatches a TUI event to the appropriate handler.
    /// Called by EmitEvent delegate from UserQuestionService and other services.
    /// </summary>
    public void DispatchEvent(TuiEvent evt)
    {
        // 无进展 watchdog 的时间源：文本增量/工具/审批/用量等所有 TUI 事件都经此漏斗，均算进展。
        // 工具开始后的静默是"等工具"，其余静默是"等模型"——状态栏文案据此区分。
        if (evt is TuiToolStart)
            _shell.AgentStatusBar.NotifyAwaitingTool();
        else
            _shell.AgentStatusBar.NotifyProgress();

        if (_transcriptPresenter.TryPresent(evt))
        {
            if (evt is TuiToolDone { Name: var name, IsError: var err, Result: var result, ToolInput: var toolInput })
            {
                // Shell 工具（Bash/PowerShell）展示前 3 行输出预览
                if (!err && !string.IsNullOrWhiteSpace(result) && IsShellTool(name))
                {
                    var shellPreview = ExtractFirstLines(result, 3);
                    if (!string.IsNullOrEmpty(shellPreview))
                        _shell.Transcript.AddStreamingNotice($"{TuiGlyphs.ToolResultPrefix} {shellPreview}", TuiPalette.FgMuted);
                }
                // LSP diagnostics inline block — after Edit/Write, show any
                // diagnostics the LSP server published for the modified file.
                TryRenderLspDiagnosticsForTool(name, toolInput);
            }
            return;
        }

        switch (evt)
        {
            case TuiSubagentProgress progress:
                // Agent 工具活动行 live 文案（无活跃行时静默）。
                _shell.Transcript.UpdateSubagentProgress(progress);
                // 状态栏 agents 徽标：Started +1，Completed/Failed -1；Running 不动
                //（同一 fork 的 Running 事件会重复到达，按生命周期配对增减）。
                _activeSubagentCount = progress.Phase switch
                {
                    OneCode.App.Query.SubagentProgressPhase.Started => _activeSubagentCount + 1,
                    OneCode.App.Query.SubagentProgressPhase.Completed or OneCode.App.Query.SubagentProgressPhase.Failed
                        => Math.Max(0, _activeSubagentCount - 1),
                    _ => _activeSubagentCount,
                };
                _shell.AgentStatusBar.SetSubagentCount(_activeSubagentCount);
                // 状态栏活动文案跟随子代理实时进度——否则与主代理工具并行派工时，
                // "执行 LS" 这类启动瞬间的快照会在子代理整个运行期间挂住不动。
                // 工具调用次数/turn/耗时由转录区子代理活动行展示，此处只留代理名+当前工具，
                // 避免重复且不触发 ActivityMaxWidth 截断把当前工具名挤掉。
                if (progress.Phase is OneCode.App.Query.SubagentProgressPhase.Started
                    or OneCode.App.Query.SubagentProgressPhase.Running)
                {
                    var currentTool = string.IsNullOrWhiteSpace(progress.CurrentToolName)
                        ? "运行中"
                        : progress.CurrentToolName;
                    _shell.AgentStatusBar.SetActivity(
                        $"[{progress.AgentName}] · {currentTool}");
                }
                else if (_activeSubagentCount == 0)
                {
                    // 子代理全部收尾、主代理尚未产生新活动事件——回到中性等待文案。
                    _shell.AgentStatusBar.SetActivity("处理中");
                }
                break;

            case TuiPermissionCheck { ToolName: var tn, Allowed: var approved, DenialReason: var reason }:
                if (!approved)
                    _shell.Transcript.AddStreamingNotice($"Permission denied: {tn} ({reason ?? "denied by user"})", TuiPalette.Error);
                break;

            case TuiToolPoolReady:
                // Tool pool initialized — no agent-context sync needed anymore.
                break;

            case TuiWorkflowRunStarted:
                _shell.Transcript.AddSystem("已批准计划，开始执行。");
                _shell.Transcript.BeginStreaming();
                _shell.ChatInput.SetBusy(true);
                _shell.SetAgentBusy(true);
                break;

            case TuiWorkflowRunEnded:
                // 与 TuiWorkflowRunStarted 配对：Build run 收尾（成功/失败/异常）复位忙碌
                // 与流式状态—— dispatcher 侧的 TuiDone 之外还需要这一步，异常路径没有 TuiDone。
                _shell.Transcript.EndStreaming();
                _shell.ChatInput.SetBusy(false);
                _shell.SetAgentBusy(false);
                _shell.FocusChatInput();
                break;

            case TuiDone { InputTokens: var inp, OutputTokens: var out_, TerminalReason: var reason, TurnsCompleted: var tc, CacheReadTokens: var cr, CacheWriteTokens: var cw, TransactionRolledBack: var rolledBack, ValidationFailureSummary: var validationSummary }:
                // InputTokens 是 API 返回的本次请求完整上下文快照（含缓存命中部分，见
                // UsageDetails.InputTokenCount 契约），不是增量。多轮间直接覆盖即可，
                // 否则累加会让上下文占用百分比随轮次虚高（实际只增长几 K，显示却翻倍）。
                // OutputTokens / CacheReadTokens / CacheWriteTokens 是增量，仍累加。
                _inputTokens = inp;
                _outputTokens += out_;
                _cacheReadTokens += cr;
                _cacheWriteTokens += cw;
                _lastRoundInputTokens = _inputTokens;
                _shell.SessionContextBar.SetTokens(_inputTokens, _outputTokens);
                _shell.SessionContextBar.SetContextUsage(_maxContextTokens, _lastRoundInputTokens);

                // run 收尾清零 agents 徽标：后台 fork 的终态事件在父 channel 关闭后
                // 无法到达（TryWrite 安全 no-op），不重置会残留幽灵计数。
                _activeSubagentCount = 0;
                _shell.AgentStatusBar.SetSubagentCount(0);

                // Render terminal reason distinctly
                switch (reason)
                {
                    case RunTerminalReason.TurnLimitReached:
                        _shell.Transcript.AddSystem($"(reached max turns: {tc})");
                        break;
                    case RunTerminalReason.BudgetExceeded:
                        _shell.Transcript.AddSystem($"(budget exceeded after {tc} turns)");
                        break;
                    case RunTerminalReason.Cancelled:
                        _shell.Transcript.AddSystem($"(cancelled after {tc} turns)");
                        break;
                    case RunTerminalReason.ValidationFailed:
                        _shell.Transcript.AddSystem(rolledBack
                            ? "(validation failed — file changes rolled back)"
                            : "(validation failed — file changes kept for your review)");
                        if (!string.IsNullOrEmpty(validationSummary))
                            _shell.Transcript.AddSystem(validationSummary!);
                        break;
                    case RunTerminalReason.AgentException:
                        _shell.Transcript.AddSystem($"(agent error after {tc} turns)");
                        break;
                    case RunTerminalReason.PermissionRefused:
                        _shell.Transcript.AddSystem($"(permission refused after {tc} turns)");
                        break;
                    case RunTerminalReason.Blocked:
                        _shell.Transcript.AddSystem("(build blocked by workspace conflict or external dependency)");
                        break;
                }

                if (rolledBack && reason != RunTerminalReason.ValidationFailed)
                    _shell.Transcript.AddSystem($"(file changes rolled back)");

                // PLAN 收尾兜底：run 正常结束但侧栏从未出现计划（模型未调 SubmitPlan
                // 交付，或提交被质量门槛/安全扫描打回后改用文字收尾）。无兜底时用户
                // 只看到文字总结，无法知道规划流程没有走完、侧栏设计为何没出现。
                if ((_ctx.ModeController?.Mode ?? WorkingMode.Build) == WorkingMode.Plan
                    && reason is RunTerminalReason.Completed or RunTerminalReason.TurnLimitReached
                    && _shell.ActivePlan is null)
                {
                    _shell.Transcript.AddSystem(
                        "模型未提交计划（未调用 SubmitPlan）。可发送「请用 SubmitPlan 提交计划」让它补交，或直接输入你的意见让它继续。");
                }
                break;

            case TuiError { Message: var msg }:
                // GOAL 编排中的成员级错误是中途事件：不能走 AddError
                // （其内部会强制结束流式状态），否则后续工具事件落入 committed
                // 路径，完整 JSON 平铺外显。查询运行中改为流式错误通知；
                // 流式生命周期由查询循环的 finally 统一收尾。
                if (_isQueryRunning)
                    _shell.Transcript.AddStreamingNotice(msg, TuiPalette.Error);
                else
                    _shell.Transcript.AddError(msg);
                break;

            case TuiTurnStarted { TurnNumber: var tn }:
                _turnNumber = tn;
                _shell.SessionContextBar.SetTurn(tn);
                if (tn > 1)
                    _shell.Transcript.ContinueStreaming();
                break;

            case TuiTurnCompleted { TurnNumber: var tn }:
                _turnNumber = tn;
                _shell.SessionContextBar.SetTurn(tn);
                break;

            case TuiSuggestions { Items: var items }:
                // 下一步提示建议 — turn 完成后由规则引擎生成，在输入框显示为占位符
                _shell.ChatInput.SetSuggestions(items);
                break;

            // 事件驱动审批请求 — TUI 自主渲染审批组件，通过 ResponseSource 回传决策
            case TuiApprovalRequest { ToolName: var tn, ToolInput: var ti, ResponseSource: var rs }:
                _ = HandleApprovalRequestAsync(tn, ti, rs);
                break;

            // LSP 诊断变更 — 刷新状态栏 LSP 指示器（服务器数/错误数/警告数）
            case TuiLspDiagnosticsChanged:
                UpdateLspStatusBar();
                break;

            // MCP 服务器集合变化 — 刷新状态栏三态指示（唯一实时出口；
            // 欢迎页已不渲染 MCP 行，失败明细由一次性 startup hint 给出）。
            case TuiMcpServersChanged:
                UpdateMcpStatusBar();
                break;

            // 用户提问请求 — 显示交互式提问对话框
            case TuiUserQuestionRequest { Question: var q, Options: var opts, ResponseSource: var rs }:
                _ = HandleUserQuestionRequestAsync(q, opts, rs);
                break;

            // 多问题向导请求 — 显示向导式多问题交互
            case TuiQuestionWizardRequest { Title: var title, Questions: var questions, ResponseSource: var rs }:
                _ = HandleQuestionWizardRequestAsync(title, questions, rs);
                break;

            // GOAL 预算耗尽交互请求 — 弹出 InlineSelector 询问继续（追加预算）/跳过/暂停/终止
            case TuiGoalBudgetExhaustedRequest budgetRequest:
                _ = HandleGoalBudgetExhaustedRequestAsync(budgetRequest);
                break;

            case TuiWorkingDirectoryChanged cwdChanged:
                _ = _shell.RefreshSessionContextBarAsync(cwdChanged.WorkingDirectory);
                break;

            case TuiGoalPlanUpdated planUpdated:
                _shell.ShowGoalPlan(
                    planUpdated.Steps,
                    planUpdated.Executions,
                    planUpdated.CurrentIndex,
                    planUpdated.Usage,
                    planUpdated.Budget);
                break;
        }
    }

    /// <summary>
    /// Pulls the latest LSP server status and diagnostics from TuiContext
    /// and updates the status bar's LSP indicator.
    /// </summary>
    private void UpdateLspStatusBar()
    {
        if (_ctx.GetLspServerStatus is null || _ctx.GetLspDiagnostics is null) return;
        var status = _ctx.GetLspServerStatus();
        var diagnostics = _ctx.GetLspDiagnostics();
        var serverCount = status.Count(s => s.IsRunning);
        var errors = diagnostics.Count(d => d.Severity == LspDiagnosticSeverity.Error);
        var warnings = diagnostics.Count(d => d.Severity == LspDiagnosticSeverity.Warning);
        _shell.AgentStatusBar.SetLspStatus(serverCount, errors, warnings);
    }

    /// <summary>
    /// Pulls the MCP connection tri-state summary from TuiContext
    /// and updates the status bar's MCP indicator
    /// (连接中 x/y / 已连 n / 失败 k；零活动时隐藏)。
    /// </summary>
    private void UpdateMcpStatusBar()
    {
        if (_ctx.GetMcpConnectionSummary is not { } getSummary) return;
        _shell.AgentStatusBar.SetMcpStatus(getSummary());
    }

    /// <summary>
    /// 异步处理审批请求 — 构造提示文本、显示审批 UI、回传决策。
    /// </summary>
    private async Task HandleApprovalRequestAsync(
        string toolName,
        string? toolInput,
        TaskCompletionSource<ApprovalDecision> responseSource)
    {
        try
        {
            var (title, message, allowApprovals) = BuildApprovalPrompt(toolName, toolInput);
            var request = new PermissionPromptRequest(title, message, allowApprovals);
            var result = await ShowPermissionPromptAsync(request, CancellationToken.None).ConfigureAwait(false);

            // Parse failure: AllowApprovals is false — UI only offered Deny, but still map defensively.
            var decision = !allowApprovals
                ? ApprovalDecision.Deny
                : result.Decision switch
                {
                    PermissionPromptDecision.Allow => ApprovalDecision.AllowOnce,
                    PermissionPromptDecision.AllowAlways => ApprovalDecision.AllowAlways,
                    PermissionPromptDecision.AllowAllConversation => ApprovalDecision.AllowAllConversation,
                    _ => ApprovalDecision.Deny,
                };

            if (decision == ApprovalDecision.AllowAllConversation)
                ApplySessionAllowAll();

            responseSource.TrySetResult(decision);
        }
        catch (Exception ex)
        {
            responseSource.TrySetException(ex);
        }
    }

    /// <summary>
    /// 会话级全放行：把 PermissionModeProvider 运行时覆盖切到 BypassPermissions
    /// （不写配置，重启后恢复），并在会话记录留痕。由 TuiHostConfigurator 注入委托，
    /// 未配置时（测试/无宿主场景）仅记录提示。
    /// </summary>
    private void ApplySessionAllowAll()
    {
        _applySessionAllowAll?.Invoke();
        Invoke(() => _shell.Transcript.AddSystem(
            "已允许本次对话全部工具调用（不再询问；重启 OneCode 后恢复，/permissions 可随时改回）"));
    }

    /// <summary>
    /// 构造审批提示文本。解析失败时 fail closed（不允许批准）。
    /// </summary>
    private static (string Title, string Message, bool AllowApprovals) BuildApprovalPrompt(
        string toolName, string? toolInput)
    {
        var parsed = ToolInputParser.Parse(toolInput);
        if (!parsed.Ok)
        {
            return (
                "⚠ 参数无法解析",
                $"工具：{toolName}\n参数无法解析，已禁止批准。\n原文：{parsed.Error}",
                false);
        }

        var (title, message) = toolName switch
        {
            "Bash" => BuildShellPrompt(toolName, parsed.Input),
            _ when ToolNames.FileWriteTools.Contains(toolName) => BuildFilePrompt(toolName, parsed.Input),
            _ => BuildGenericPrompt(toolName, parsed.Input),
        };
        return (title, message, true);
    }

    internal static (string, string) BuildShellPrompt(string toolName, System.Text.Json.JsonElement input)
    {
        var command = GetStringProp(input, "command") ?? "";
        var display = command.Length > 200 ? command[..200] + "\u2026" : command;

        var warning = toolName == "Bash"
            ? BashCommandClassifier.GetDestructiveCommandWarning(command)
            : PowerShellCommandClassifier.GetDestructiveCommandWarning(command);

        var sb = new System.Text.StringBuilder();
        sb.Append("工具：").Append(toolName);
        if (!string.IsNullOrEmpty(warning))
            sb.Append("\n>>> 危险：").Append(warning).Append(" <<<");
        sb.Append("\n命令：\n  $ ").Append(display);

        // 标题使用真实工具名（Bash），与执行后对话列表里的工具行一致——
        // 旧实现硬编码 "Shell 命令"，审批提示与调用记录对不上。
        return (warning is not null ? "！ 危险命令 !" : $"$ {toolName} 命令", sb.ToString());
    }

    private static (string, string) BuildFilePrompt(string toolName, System.Text.Json.JsonElement input)
    {
        var filePath = OneCode.Core.Tools.ToolArgumentExtractor.ExtractFilePath(input) ?? "unknown";
        var fileName = Path.GetFileName(filePath);
        var exists = File.Exists(filePath);
        var existsLabel = exists ? "已存在" : "新建";
        var message = $"工具：{toolName}\n文件：{fileName}\n路径：{filePath}\n状态：{existsLabel}";
        return ($"{toolName} 文件", message);
    }

    private static (string, string) BuildGenericPrompt(string toolName, System.Text.Json.JsonElement input)
    {
        var description = OneCode.Core.Tools.ToolArgumentExtractor.BuildToolDescription(toolName, input);
        var message = $"工具：{toolName}\n操作：{description}";
        return ("权限请求", message);
    }

    private static string? GetStringProp(System.Text.Json.JsonElement el, string name)
    {
        if (el.ValueKind == System.Text.Json.JsonValueKind.Object
            && el.TryGetProperty(name, out var prop)
            && prop.ValueKind == System.Text.Json.JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    /// <summary>
    /// 判断是否为 Shell 类工具（Bash/PowerShell），这类工具的输出在非 verbose 模式下也展示前 3 行预览。
    /// </summary>
    private static bool IsShellTool(string toolName)
        => toolName.Equals("Bash", StringComparison.OrdinalIgnoreCase)
        || toolName.Equals("bash", StringComparison.OrdinalIgnoreCase);

    /// <summary>提取文本前 N 行（用于 Shell 工具输出预览）。</summary>
    private static string ExtractFirstLines(string text, int lineCount)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (OneCode.Core.Tools.DisplayJsonSerializer.TryUnwrapEnvelope(text, out _, out var unwrapped)
            && !string.IsNullOrEmpty(unwrapped))
        {
            text = unwrapped;
        }
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length <= lineCount) return text.Trim();
        return string.Join("\n", lines.Take(lineCount)).Trim() + $"\n{TuiGlyphs.Ellipsis} ({lines.Length - lineCount} more lines)";
    }

    /// <summary>
    /// After EditTool/WriteTool completes, renders an inline LSP diagnostics
    /// block if the LSP server has published any diagnostics for the modified
    /// file. No-op when LSP services are unavailable or the tool is not a
    /// file-editing tool.
    /// </summary>
    private void TryRenderLspDiagnosticsForTool(string toolName, string? toolInput)
    {
        if (!IsFileEditTool(toolName))
            return;
        if (_ctx.GetLspDiagnostics is null)
            return;

        var parsed = ToolInputParser.Parse(toolInput);
        if (!parsed.Ok)
            return;

        var filePath = OneCode.Core.Tools.ToolArgumentExtractor.ExtractFilePath(parsed.Input);
        if (string.IsNullOrEmpty(filePath))
            return;

        var allDiagnostics = _ctx.GetLspDiagnostics();
        _shell.Transcript.AddLspDiagnostics(allDiagnostics, filePath);
    }

    /// <summary>Checks if the tool modifies files and should trigger LSP diagnostics rendering.</summary>
    private static bool IsFileEditTool(string toolName)
        => ToolNames.FileWriteTools.Contains(toolName);
}
