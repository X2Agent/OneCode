using OneCode.App.Services.Interaction;
using OneCode.Core.Goals;

namespace OneCode.Tui;

/// <summary>
/// Session/config modal handlers for <see cref="OneCodeToplevel"/>.
/// Hosts the session chooser, settings overlay, and permission prompt flows.
/// </summary>
public sealed partial class OneCodeToplevel
{
    public void ConfigureSessionModals(
        Func<CancellationToken, Task<string?>> showResumeChooserAsync,
        Func<string, CancellationToken, Task> resumeSessionAsync)
    {
        _showResumeChooserAsync = showResumeChooserAsync;
        _resumeSessionAsync = resumeSessionAsync;
    }

    public void ConfigureSettingsModal(
        Func<CancellationToken, Task<bool>> showSettingsOverlayAsync,
        Func<CancellationToken, Task<string>> applySettingsAsync)
    {
        _showSettingsOverlayAsync = showSettingsOverlayAsync;
        _applySettingsAsync = applySettingsAsync;
    }

    /// <summary>
    /// 配置会话级全放行委托：审批弹窗选择「本次对话全部允许」时调用。
    /// 由 TuiHostConfigurator 注入（翻转 PermissionModeProvider 运行时覆盖到
    /// BypassPermissions，不写配置）；未注入时仅记录会话提示，不改权限状态。
    /// </summary>
    public void ConfigureSessionPermissionEscalation(Action applySessionAllowAll)
    {
        _applySessionAllowAll = applySessionAllowAll;
    }

    /// <summary>
    /// 配置 MCP 配置页弹层委托。<paramref name="showMcpConfigAsync"/> 返回保存摘要
    /// （用户取消时为 null）；未配置时 /mcp 退回 /mcp list 文本输出。
    /// </summary>
    public void ConfigureMcpConfigModal(Func<CancellationToken, Task<string?>> showMcpConfigAsync)
    {
        _showMcpConfigAsync = showMcpConfigAsync;
    }

    public async Task<PermissionPromptResult> ShowPermissionPromptAsync(PermissionPromptRequest request, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<PermissionPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        _app.Invoke(() =>
        {
            // Fail-closed: malformed tool input must not offer Allow / AllowAlways.
            List<InlineSelectorOption> options = [];
            if (request.AllowApprovals)
            {
                options.Add(new("allow", "允许执行 (仅本次)", request.Message));
                options.Add(new("always", "始终允许此工具 (不再提示)"));
                options.Add(new("session-all", "本次对话全部允许 (所有工具不再提示)"));
            }
            options.Add(new("deny", "拒绝", request.AllowApprovals ? null : request.Message));
            var selector = new InlineSelector(request.Title, options);
            _shell.ShowInlineSelector(selector);
            _ = selector.ResultTask.ContinueWith(t =>
            {
                _app.Invoke(() =>
                {
                    // Fail-closed：收尾路径自己抛异常时也必须回传拒绝。
                    // 这个 lambda 的结果无人观察，异常一旦逸出，tcs 永不完成 ——
                    // 用户按下的那一次审批会变成整轮查询永久卡死，比误拒更糟。
                    try
                    {
                        _shell.DismissInlineSelector();
                        if (t.IsCompletedSuccessfully)
                        {
                            var result = t.Result;
                            var decision = result.SelectedId switch
                            {
                                "allow" => new PermissionPromptResult(PermissionPromptDecision.Allow),
                                "always" => new PermissionPromptResult(PermissionPromptDecision.AllowAlways),
                                "session-all" => new PermissionPromptResult(PermissionPromptDecision.AllowAllConversation),
                                _ => new PermissionPromptResult(PermissionPromptDecision.Deny),
                            };
                            tcs.TrySetResult(decision);
                        }
                        else
                        {
                            tcs.TrySetResult(new PermissionPromptResult(PermissionPromptDecision.Deny));
                        }
                    }
                    catch (Exception)
                    {
                        tcs.TrySetResult(new PermissionPromptResult(PermissionPromptDecision.Deny));
                    }
                });
            }, TaskScheduler.Default);
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task HandleSessionChooserAsync(CancellationToken ct)
    {
        if (_showResumeChooserAsync is null || _resumeSessionAsync is null)
        {
            var result = await _ctx.ExecuteCommand("/session list", ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result))
            {
                Invoke(() =>
                {
                    _shell.Transcript.AddUserMessage("/session");
                    _shell.Transcript.AddCommandResult(result);
                });
            }
            return;
        }

        string? selectedSessionId;
        try
        {
            selectedSessionId = await _showResumeChooserAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Invoke(() => _shell.Transcript.AddError($"Session chooser failed: {ex.Message}"));
            return;
        }

        if (string.IsNullOrWhiteSpace(selectedSessionId))
        {
            return;
        }

        try
        {
            await _resumeSessionAsync(selectedSessionId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Invoke(() => _shell.Transcript.AddError($"Failed to switch session: {ex.Message}"));
        }
    }

    /// <summary>
    /// /mcp（无参）→ 打开 MCP 工具白名单配置页；未接线（非 TUI 或未配置委托）时
    /// 退回 /mcp list 文本输出。保存摘要与用户消息按 /config 的模式渲染到会话记录。
    /// </summary>
    private async Task HandleMcpConfigCommandAsync(CancellationToken ct)
    {
        if (_showMcpConfigAsync is null)
        {
            var listResult = await _ctx.ExecuteCommand("/mcp list", ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(listResult))
            {
                Invoke(() =>
                {
                    _shell.Transcript.AddUserMessage("/mcp");
                    _shell.Transcript.AddCommandResult(listResult);
                });
            }
            return;
        }

        string? summary;
        try
        {
            summary = await _showMcpConfigAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Invoke(() => _shell.Transcript.AddError($"MCP config overlay failed: {ex.Message}"));
            return;
        }

        if (summary is null)
            return;

        Invoke(() =>
        {
            _shell.Transcript.AddUserMessage("/mcp");
            _shell.Transcript.AddCommandResult(summary);
        });
    }

    private async Task HandleConfigCommandAsync(CancellationToken ct)
    {
        if (_showSettingsOverlayAsync is null || _applySettingsAsync is null)
        {
            var result = await _ctx.ExecuteCommand("/config", ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result))
            {
                Invoke(() =>
                {
                    _shell.Transcript.AddUserMessage("/config");
                    _shell.Transcript.AddCommandResult(result);
                });
            }
            return;
        }

        bool shouldApply;
        try
        {
            shouldApply = await _showSettingsOverlayAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Invoke(() => _shell.Transcript.AddError($"Settings overlay failed: {ex.Message}"));
            return;
        }

        if (!shouldApply)
            return;

        try
        {
            var resultMessage = await _applySettingsAsync(ct).ConfigureAwait(false);
            Invoke(() =>
            {
                _shell.Transcript.AddUserMessage("/config");
                _shell.Transcript.AddCommandResult(resultMessage);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Invoke(() => _shell.Transcript.AddError($"Failed to apply settings: {ex.Message}"));
        }
    }

    /// <summary>
    /// 处理用户提问请求 — 显示交互式提问对话框（选项选择或自由文本输入）。
    /// </summary>
    private async Task HandleUserQuestionRequestAsync(
        string question,
        IReadOnlyList<string>? options,
        TaskCompletionSource<string?> responseSource)
    {
        try
        {
            string? answer;
            if (options is { Count: > 0 })
            {
                // 有预定义选项，使用 InlineSelector
                answer = await ShowQuestionSelectorAsync(question, options, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                // 自由文本输入，使用输入对话框
                answer = await ShowQuestionInputAsync(question, CancellationToken.None).ConfigureAwait(false);
            }

            if (answer is not null)
            {
                responseSource.TrySetResult(answer);
            }
            else
            {
                responseSource.TrySetCanceled();
            }
        }
        catch (Exception ex)
        {
            responseSource.TrySetException(ex);
        }
    }

    /// <summary>
    /// 显示选项选择对话框（使用 InlineSelector）。
    /// </summary>
    private async Task<string?> ShowQuestionSelectorAsync(string question, IReadOnlyList<string> options, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        _app.Invoke(() =>
        {
            var selectorOptions = options.Select((opt, idx) => new InlineSelectorOption(
                Id: idx.ToString(CultureInfo.InvariantCulture),
                Label: opt,
                Description: null)).ToList();

            var selector = new InlineSelector(
                "向用户提问",
                selectorOptions,
                prompt: question,
                useInformationRequestCard: true);
            _shell.ShowInlineSelector(selector);

            _ = selector.ResultTask.ContinueWith(t =>
            {
                _app.Invoke(() =>
                {
                    _shell.DismissInlineSelector();
                    if (t.IsCompletedSuccessfully && !t.Result.IsDismissed)
                    {
                        var selectedIndex = int.Parse(t.Result.SelectedId, CultureInfo.InvariantCulture);
                        tcs.TrySetResult(options[selectedIndex]);
                    }
                    else
                    {
                        tcs.TrySetResult(null);
                    }
                });
            }, TaskScheduler.Default);
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// 显示自由文本输入对话框。
    /// </summary>
    private async Task<string?> ShowQuestionInputAsync(string question, CancellationToken ct)
    {
        const string answerId = "answer";
        var result = await ShowQuestionWizardAsync(
            "向用户提问",
            [new WizardQuestion(answerId, question, QuestionType.ShortText)],
            ct).ConfigureAwait(false);
        return result.IsCancelled || !result.Answers.TryGetValue(answerId, out var answer)
            ? null
            : answer;
    }

    /// <summary>
    /// 处理多问题向导请求 — 显示向导式多问题交互。
    /// </summary>
    private async Task HandleQuestionWizardRequestAsync(
        string title,
        IReadOnlyList<OneCode.Core.Tools.WizardQuestion> questions,
        TaskCompletionSource<OneCode.Core.Tools.WizardResult> responseSource)
    {
        try
        {
            var result = await ShowQuestionWizardAsync(title, questions, CancellationToken.None).ConfigureAwait(false);

            if (result.IsCancelled)
            {
                responseSource.TrySetCanceled();
            }
            else
            {
                responseSource.TrySetResult(result);
            }
        }
        catch (Exception ex)
        {
            responseSource.TrySetException(ex);
        }
    }

    /// <summary>
    /// 显示多问题向导。
    /// </summary>
    private async Task<OneCode.Core.Tools.WizardResult> ShowQuestionWizardAsync(
        string title,
        IReadOnlyList<OneCode.Core.Tools.WizardQuestion> questions,
        CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<OneCode.Core.Tools.WizardResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        _app.Invoke(() =>
        {
            var wizard = new QuestionWizard(title, questions);
            _shell.ShowQuestionWizard(wizard);

            // 首题为文本题时自动进入输入模式（选择题由挂起态直接接管键盘）
            _shell.EnterTextModeForCurrentQuestion();

            _ = wizard.ResultTask.ContinueWith(t =>
            {
                _app.Invoke(() =>
                {
                    _shell.DismissQuestionWizard();

                    if (t.IsCompletedSuccessfully)
                    {
                        tcs.TrySetResult(t.Result);
                    }
                    else
                    {
                        tcs.TrySetResult(OneCode.Core.Tools.WizardResult.Cancelled);
                    }
                });
            }, TaskScheduler.Default);
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// 处理预算耗尽时的用户决策交互 — 使用 InlineSelector 询问下一步动作。
    /// Goal 场景四选一（追加预算继续 / 跳过子目标 / 暂停保存 / 终止）；
    /// 单次 Agent 运行场景三选一（追加预算继续 / 收尾总结后结束 / 直接停止）。
    /// </summary>
    private Task HandleBudgetExhaustedRequestAsync(BudgetInteractionRequest request)
    {
        _app.Invoke(() =>
        {
            var isGoalRun = request.Context.Scope == BudgetPromptScope.GoalRun;
            List<InlineSelectorOption> options = isGoalRun
                ?
                [
                    new("continue", "继续执行（自动追加预算）", "追加 +10 次尝试额度、+100k Tokens 与 +1 小时墙钟时间，立即继续"),
                    new("skip", "跳过当前子目标", "将当前子目标标记为跳过，推进到下一个子目标"),
                    new("pause", "暂停并保存进度", "保存当前 Checkpoint 与隔离 Worktree，稍后可通过 /resume 恢复"),
                    new("abort", "终止任务", "停止当前 Goal 执行"),
                ]
                :
                [
                    new("continue", "追加预算继续", "本次运行预算上限加倍，立即继续"),
                    new("wrapup", "收尾总结后结束", "注入收尾总结轮次，汇总已完成与剩余工作后正常结束"),
                    new("abort", "直接停止", "停止本次运行"),
                ];

            var title = isGoalRun
                ? "⚠ GOAL 预算已达上限，请选择下一步操作"
                : "⚠ 执行预算已达上限，请选择下一步操作";
            var stepHint = string.IsNullOrWhiteSpace(request.Context.StepDescription)
                ? request.Context.Reason
                : $"{request.Context.Reason}\n当前子目标：{request.Context.StepDescription}";
            var selector = new InlineSelector(
                title,
                options,
                prompt: stepHint,
                useInformationRequestCard: true);
            _shell.ShowInlineSelector(selector);

            _ = selector.ResultTask.ContinueWith(t =>
            {
                _app.Invoke(() =>
                {
                    try
                    {
                        _shell.DismissInlineSelector();
                        if (t.IsCompletedSuccessfully && !t.Result.IsDismissed)
                        {
                            var decision = t.Result.SelectedId switch
                            {
                                "continue" => GoalBudgetDecision.ContinueWithExtraBudget,
                                "skip" => GoalBudgetDecision.SkipCurrentStep,
                                "wrapup" => GoalBudgetDecision.WrapUpAndStop,
                                "pause" => GoalBudgetDecision.Pause,
                                "abort" => GoalBudgetDecision.Abort,
                                _ => GoalBudgetDecision.Pause,
                            };
                            request.ResponseSource.TrySetResult(decision);
                        }
                        else
                        {
                            request.ResponseSource.TrySetResult(GoalBudgetDecision.Pause);
                        }
                    }
                    catch (Exception)
                    {
                        request.ResponseSource.TrySetResult(GoalBudgetDecision.Pause);
                    }
                });
            }, TaskScheduler.Default);
        });

        return Task.CompletedTask;
    }
}
