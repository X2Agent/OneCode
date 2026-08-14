using OneCode.Core.Build;
using OneCode.Core.Coordinator;
using OneCode.Core.Lsp;

namespace OneCode.App.Tui;

/// <summary>
/// TuiEvent dispatch for <see cref="OneCodeToplevel"/>.
/// Extracted as a partial to keep the main top-level file under the 300-line guideline.
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
        if (_transcriptPresenter.TryPresent(evt))
            return;

        switch (evt)
        {
            case TuiToolDone { Name: var name, IsError: var err, Result: var result, ToolInput: var toolInput, ToolId: var toolId }:
                _shell.Transcript.AddToolDone(name, err, toolInput, result ?? string.Empty, toolId);
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

                if (_ctx.RecordCost is { } recordCost)
                {
                    var costStr = recordCost();
                    _shell.AgentStatusBar.SetCost(costStr);
                }

                // Render terminal reason distinctly
                switch (reason)
                {
                    case BuildTerminalReason.TurnLimitReached:
                        _shell.Transcript.AddSystem($"(reached max turns: {tc})");
                        break;
                    case BuildTerminalReason.BudgetExceeded:
                        _shell.Transcript.AddSystem($"(budget exceeded after {tc} turns)");
                        break;
                    case BuildTerminalReason.Cancelled:
                        _shell.Transcript.AddSystem($"(cancelled after {tc} turns)");
                        break;
                    case BuildTerminalReason.ValidationFailed:
                        _shell.Transcript.AddSystem($"(validation failed — file changes rolled back)");
                        if (!string.IsNullOrEmpty(validationSummary))
                            _shell.Transcript.AddSystem(validationSummary!);
                        break;
                    case BuildTerminalReason.AgentException:
                        _shell.Transcript.AddSystem($"(agent error after {tc} turns)");
                        break;
                    case BuildTerminalReason.PermissionRefused:
                        _shell.Transcript.AddSystem($"(permission refused after {tc} turns)");
                        break;
                    case BuildTerminalReason.ClarificationRequired:
                        _shell.Transcript.AddSystem("(build paused until scope clarification is confirmed)");
                        break;
                    case BuildTerminalReason.Blocked:
                        _shell.Transcript.AddSystem("(build blocked by workspace conflict or external dependency)");
                        break;
                }

                if (rolledBack && reason != BuildTerminalReason.ValidationFailed)
                    _shell.Transcript.AddSystem($"(file changes rolled back)");
                break;

            case TuiError { Message: var msg }:
                _shell.Transcript.EndStreaming();
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

            case TuiTeamPlanApproval approval:
                HandleTeamPlanApprovalNotification(approval);
                break;

            case TuiTeamDelivery { Report: var report }:
                _shell.Transcript.UpdateModeProgress(new TuiModeProgress(
                    WorkingMode.Team,
                    report.Committed
                        ? $"团队任务已完成：修改 {report.Changes.Files.Count} 个文件，验证通过"
                        : "团队任务未完成，文件修改已回滚",
                    report.Committed ? ModeProgressState.Completed : ModeProgressState.Failed));
                if (!string.IsNullOrWhiteSpace(report.Summary))
                    _shell.Transcript.AddSystem(report.Summary);
                if (!report.Committed)
                {
                    foreach (var gate in report.Gates.Where(g => g.Required && g.Status != QualityGateStatus.Passed))
                    {
                        _shell.Transcript.AddSystem(
                            $"质量门 {gate.GateId} [{gate.Kind}]：{gate.Status} — {gate.Summary}");
                        foreach (var evidence in gate.Evidence.Take(3))
                            _shell.Transcript.AddSystem($"  证据：{evidence}");
                    }
                }
                break;

            case TuiSuggestions { Items: var items }:
                // 下一步提示建议 — turn 完成后由规则引擎生成，在输入框显示为占位符
                _shell.ChatInput.SetSuggestions(items);
                break;

            // 事件驱动审批请求 — TUI 自主渲染审批组件，通过 ResponseSource 回传决策
            case TuiApprovalRequest { RequestId: var rid, ToolName: var tn, ToolInput: var ti, ResponseSource: var rs }:
                _ = HandleApprovalRequestAsync(tn, ti, rs);
                break;

            // LSP 诊断变更 — 刷新状态栏 LSP 指示器（服务器数/错误数/警告数）
            case TuiLspDiagnosticsChanged:
                UpdateLspStatusBar();
                break;

            // 用户提问请求 — 显示交互式提问对话框
            case TuiUserQuestionRequest { Question: var q, Options: var opts, ResponseSource: var rs }:
                _ = HandleUserQuestionRequestAsync(q, opts, rs);
                break;

            // 多问题向导请求 — 显示向导式多问题交互
            case TuiQuestionWizardRequest { Title: var title, Questions: var questions, ResponseSource: var rs }:
                _ = HandleQuestionWizardRequestAsync(title, questions, rs);
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
    /// Displays the Team plan approval notification. Approval decisions are now handled
    /// by the MAF RequestPort workflow (TeamApprovalWorkflowHost) via IClarificationInteractionService,
    /// so the TUI only shows a status message without blocking on a TaskCompletionSource.
    /// </summary>
    private void HandleTeamPlanApprovalNotification(TuiTeamPlanApproval approval)
    {
        var taskSummary = approval.Tasks.Count == 0
            ? approval.Summary
            : string.Join("；", approval.Tasks.Take(3)) + (approval.Tasks.Count > 3 ? "…" : string.Empty);
        _shell.Transcript.AddSystem($"团队 {approval.TeamName} 计划审批中: {taskSummary}");
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
                    _ => ApprovalDecision.Deny,
                };

            responseSource.TrySetResult(decision);
        }
        catch (Exception ex)
        {
            responseSource.TrySetException(ex);
        }
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
            "Bash" or "PowerShell" => BuildShellPrompt(toolName, parsed.Input),
            _ when ToolNames.FileWriteTools.Contains(toolName) => BuildFilePrompt(toolName, parsed.Input),
            _ => BuildGenericPrompt(toolName, parsed.Input),
        };
        return (title, message, true);
    }

    private static (string, string) BuildShellPrompt(string toolName, System.Text.Json.JsonElement input)
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

        return (warning != null ? "！ 危险命令 !" : "$ Shell 命令", sb.ToString());
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
        || toolName.Equals("PowerShell", StringComparison.OrdinalIgnoreCase)
        || toolName.Equals("powershell", StringComparison.OrdinalIgnoreCase)
        || toolName.Equals("bash", StringComparison.OrdinalIgnoreCase);

    /// <summary>提取文本前 N 行（用于 Shell 工具输出预览）。</summary>
    private static string ExtractFirstLines(string text, int lineCount)
    {
        if (string.IsNullOrEmpty(text)) return "";
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
