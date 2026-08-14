namespace OneCode.App.Tui;

/// <summary>
/// Session/config modal handlers for <see cref="OneCodeToplevel"/>.
/// Extracted as a partial to keep the main file under the 300-line guideline.
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

    public async Task<PermissionPromptResult> ShowPermissionPromptAsync(PermissionPromptRequest request, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<PermissionPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        _app.Invoke(() =>
        {
            // Fail-closed: malformed tool input must not offer Allow / AllowAlways.
            var options = new List<InlineSelectorOption>();
            if (request.AllowApprovals)
            {
                options.Add(new("allow", "允许执行 (仅本次)", request.Message));
                options.Add(new("always", "始终允许此工具 (不再提示)"));
            }
            options.Add(new("deny", "拒绝", request.AllowApprovals ? null : request.Message));
            var selector = new InlineSelector(request.Title, options);
            _shell.ShowInlineSelector(selector);
            _ = selector.ResultTask.ContinueWith(t =>
            {
                _app.Invoke(() =>
                {
                    _shell.DismissInlineSelector();
                    if (t.IsCompletedSuccessfully)
                    {
                        var result = t.Result;
                        var decision = result.SelectedId switch
                        {
                            "allow" => new PermissionPromptResult(PermissionPromptDecision.Allow),
                            "always" => new PermissionPromptResult(PermissionPromptDecision.AllowAlways),
                            _ => new PermissionPromptResult(PermissionPromptDecision.Deny),
                        };
                        tcs.TrySetResult(decision);
                    }
                    else
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
        TaskCompletionSource<string> responseSource)
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

            // 检查第一个问题是否是文本输入类型，如果是则自动进入输入模式
            if (questions.Count > 0)
            {
                var firstQuestion = questions[0];
                if (firstQuestion.Type == QuestionType.ShortText)
                {
                    // 短文本题：进入输入模式
                    _shell.EnterShortTextModeForWizard();
                }
                else if (firstQuestion.Type == QuestionType.LongText)
                {
                    // 长文本题：进入长文本输入模式
                    _shell.EnterLongTextModeForWizard();
                }
            }

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
}
