namespace OneCode.App.Tui;

/// <summary>
/// Submit dispatch pipeline for <see cref="OneCodeToplevel"/>.
/// Pipeline: OnUserSubmitted → (immediate | queued | normal) → HandleSubmitAsync → HandleSubmitCoreAsync
///   - Immediate commands bypass the query queue and run without cancelling ongoing queries.
///   - Non-immediate slash commands are queued while a query is running (C-07).
///   - Normal input cancels any ongoing query and starts a new one.
///   - In-transcript immediate commands (/find, /diff) live in OneCodeToplevel.Commands.cs.
/// </summary>
public sealed partial class OneCodeToplevel
{
    private void OnUserSubmitted(string text)
    {
        // Detect OSC 52 image paste from terminal clipboard.
        // Route through ChatInputView.AddImageBytes so the image enters the same
        // unified pipeline (ImagePipeline processing + _pendingImages storage)
        // as clipboard/file-list pastes.
        var oscData = Osc52Detector.Detect(text);
        var hadOsc52Image = false;
        if (oscData is { IsImage: true })
        {
            hadOsc52Image = true;
            _shell.ChatInput.AddImageBytes(oscData.Data);
            text = Osc52Detector.StripOsc52(text);
        }

        // Capture pending images from the prompt BEFORE clearing.
        var images = _shell.ChatInput.TakePendingImages();

        // If an OSC 52 image was detected but rejected by AddImageBytes
        // (model doesn't support multimodal), images will be empty and the
        // rejection event was already fired. Don't submit "[Image attached]"
        // as a phantom text message.
        if (hadOsc52Image && images.Count == 0)
        {
            return;
        }

        // Check multimodal support before submitting images.
        if (images.Count > 0 && !_ctx.ModelCatalog.SupportsAttachment(_ctx.Model))
        {
            Invoke(() => _shell.Transcript.AddError(
                $"The current model ({_ctx.Model}) does not support image attachments. " +
                "Please switch to a multimodal model (e.g., claude-sonnet-4) or remove the images."));
            return;
        }

        // If images are present and text is empty, provide a placeholder.
        if (images.Count > 0 && string.IsNullOrWhiteSpace(text))
        {
            text = "[Image attached]";
        }

        // Immediate commands execute without cancelling the current query.
        // Show the user line here — OnUserSubmitted previously returned before
        // AddUserMessageDirect, so /find and /diff never appeared in the transcript.
        if (text.StartsWith('/') && _ctx.IsImmediateCommand?.Invoke(text) == true)
        {
            _shell.Transcript.AddUserMessageDirect(text);
            _userMessageShown = true;
            _ = HandleImmediateCommandAsync(text);
            return;
        }

        // Queue non-immediate input while query is running.
        // 支持斜杠命令和自然语言——query 运行时输入自动入队，完成后自动出队执行。
        // 图片随文本一同入队，出队时原样传递给 HandleSubmitCoreAsync，避免丢失。
        // 用户气泡在 DrainInputQueueAsync 出队时再画，避免队列堆积未执行消息。
        if (_isQueryRunning && _ctx.InputQueue is not null)
        {
            _ctx.InputQueue.Enqueue(text, images.Count > 0 ? images : null);
            Invoke(() => _shell.Transcript.AddSystem($"Queued: {text}"));
            return;
        }

        _queryCts.Cancel();
        _queryCts = CancellationTokenSource.CreateLinkedTokenSource(_ctx.ExternalCancellation);

        _shell.Transcript.EndStreaming();

        _userMessageShown = false;
        // Show user message immediately on the UI thread for both slash commands
        // and natural language. Previously slash commands deferred display to
        // HandleSubmitCoreAsync (background thread via Invoke), causing a visible
        // delay where the input seemed to hang before appearing.
        _shell.Transcript.AddUserMessageDirect(text);
        _userMessageShown = true;

        _ = HandleSubmitAsync(text, images.Count > 0 ? images : null, _queryCts.Token);
    }

    /// <summary>
    /// 解析命令执行时的忙碌状态标签：优先使用命令声明的 <see cref="ICommand.ProgressMessage"/>，
    /// 未声明时回退到「执行 /{name}」。
    /// </summary>
    private string GetProgressLabel(string text)
    {
        var message = _ctx.GetProgressMessage?.Invoke(text);
        if (!string.IsNullOrWhiteSpace(message))
            return message;
        var name = text.TrimStart('/').Split(' ')[0];
        return $"执行 /{name}";
    }

    private async Task HandleImmediateCommandAsync(string text)
    {
        // /find is Immediate so it runs here (not HandleSubmitCoreAsync). Scroll
        // the transcript in-process; FindCommand itself is only for discovery /
        // non-TUI fallback.
        if (TryHandleFindInTranscript(text))
            return;

        // /diff (no args) — open ReviewOverlay in-process.
        if (TryHandleDiffOverlay(text))
            return;

        // /keybindings list — open KeybindingsOverlay in-process.
        if (TryHandleKeybindingsListOverlay(text))
            return;

        // Bare /session — open the resume chooser in-process. SessionCommand is
        // Immediate, so HandleSubmitCoreAsync's bare-/session interception is
        // unreachable from the TUI; mirror it here (same contract as bare /config's
        // overlay path). Subcommands (list/new/switch/close) still run below.
        if (text.Trim().Equals("/session", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSessionChooserAsync(_ctx.ExternalCancellation).ConfigureAwait(false);
            return;
        }

        // User message already shown by OnUserSubmitted before this method runs.
        // /find and /diff (no args) are handled above and return early.
        var progressLabel = GetProgressLabel(text);
        Invoke(() => { _shell.ChatInput.SetBusy(true); _shell.Transcript.BeginStreaming(); _shell.SetAgentBusy(true, progressLabel); });
        try
        {
            var result = await _ctx.ExecuteCommand(text, _ctx.ExternalCancellation).ConfigureAwait(false);
            if (result is not null)
                Invoke(() =>
                {
                    _shell.Transcript.AddCommandResult(result);
                    RefreshSessionName();
                });
        }
        catch (Exception ex)
        {
            Invoke(() => _shell.Transcript.AddError($"Command error: {ex.Message}"));
        }
        finally
        {
            Invoke(() => { _shell.Transcript.EndStreaming(); _shell.ChatInput.SetBusy(false); _shell.SetAgentBusy(false); _shell.FocusChatInput(); });
        }
    }

    private void OnQuitRequested()
    {
        _queryCts.Cancel();
        ExitCode = 0;
        RequestStop();
    }

    /// <summary>
    /// Esc (while busy) or chat:killAgents — cancel the running agent/query without exiting.
    /// 仅取消 CancellationToken，让 agent 自行检测并退出；用户可继续输入。
    /// 同时清空输入队列——用户明确要停下来，待执行的排队输入不应继续执行。
    /// </summary>
    private void OnInterruptRequested()
    {
        if (_isQueryRunning)
        {
            _userInterruptNotified = true;
            _queryCts.Cancel();
            var queueCount = _ctx.InputQueue?.Count ?? 0;
            if (queueCount > 0)
            {
                _ctx.InputQueue?.Clear();
                _shell.Transcript.AddSystem($"已中断当前 agent，队列中 {queueCount} 条待执行输入已清空。");
            }
            else
            {
                _shell.Transcript.AddSystem("已中断当前 agent。");
            }
        }
    }

    private void OnImagePasteRequested()
    {
        // Ctrl+V image paste: trigger OSC 52 paste flow.
        // The actual image data comes via the Submitted event handler (Osc52Detector).
        // Here we just signal the user to paste.
        Invoke(() => _shell.Transcript.AddSystem("Paste an image (Ctrl+V in your terminal)..."));
    }

    // Submit dispatch
    //
    // Slash commands are dispatched locally first (CommandRegistry);
    // only unknown input (and non-slash messages) go to the Anthropic API.

    private async Task HandleSubmitAsync(string text, IReadOnlyList<string>? images, CancellationToken ct)
    {
        _isQueryRunning = true;
        try
        {
            await HandleSubmitCoreAsync(text, images, ct).ConfigureAwait(false);
        }
        finally
        {
            // 保持 _isQueryRunning = true 直到队列排空，避免队列消费期间用户输入
            // 被立即处理（绕过入队路径），与 DrainInputQueueAsync 中的
            // HandleSubmitCoreAsync 并发执行造成 _queryCts / Transcript 竞态。
            // 嵌套 finally 确保 DrainInputQueueAsync 抛出时 _isQueryRunning 仍能复位。
            try
            {
                await DrainInputQueueAsync().ConfigureAwait(false);
            }
            finally
            {
                _isQueryRunning = false;
            }
        }
    }

    /// <summary>
    /// 输入队列自动消费：每次对话完成后取下一条输入继续执行。
    /// 队列在 query 运行时由用户输入自动填充，也可通过 /queue 命令主动预排。
    /// 内存单队列，不持久化——进程内有效。
    /// </summary>
    private async Task DrainInputQueueAsync()
    {
        if (_ctx.InputQueue is null)
            return;

        // 如果 agent 被中断（_queryCts 已取消），不排空队列——
        // OnInterruptRequested 已清空队列，这里直接退出。
        while (!_ctx.ExternalCancellation.IsCancellationRequested
               && !_queryCts.IsCancellationRequested)
        {
            var next = _ctx.InputQueue.Dequeue();
            if (next is null)
                return;

            // Dequeue 与执行之间可能发生中断：Clear 清不掉已出队项，直接丢弃并退出。
            if (_queryCts.IsCancellationRequested
                || _ctx.ExternalCancellation.IsCancellationRequested)
                return;

            // 显示系统提示 + 用户气泡（入队时只写 Queued:，出队时再画用户行）
            var preview = next.Text.Length > 60 ? next.Text[..60] + "..." : next.Text;
            var queuedText = next.Text;
            Invoke(() =>
            {
                _shell.Transcript.AddSystem($"▶ Queue: {preview}");
                _shell.Transcript.AddUserMessageDirect(queuedText);
            });
            _userMessageShown = true;

            // 关联 _queryCts.Token：用户中断时正在执行的队列项也会被取消
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                _ctx.ExternalCancellation, _queryCts.Token);
            await HandleSubmitCoreAsync(next.Text, next.Images, cts.Token).ConfigureAwait(false);
        }
    }

    private async Task HandleSubmitCoreAsync(string text, IReadOnlyList<string>? images, CancellationToken ct)
    {
        // User message is already shown by OnUserSubmitted (or DrainInputQueueAsync
        // for queued items). Do not reset _userMessageShown — that caused PromptResult
        // commands (/review) and AI fallthrough to double-render the user line.

        if (text.StartsWith('/'))
        {
            var name = text.TrimStart('/').Split(' ')[0].ToLowerInvariant();

            if (name == "session" && text.Trim().Equals("/session", StringComparison.OrdinalIgnoreCase))
            {
                await HandleSessionChooserAsync(ct).ConfigureAwait(false);
                return;
            }

            if (name == "config" && text.Trim().Equals("/config", StringComparison.OrdinalIgnoreCase))
            {
                await HandleConfigCommandAsync(ct).ConfigureAwait(false);
                return;
            }

            // PromptResult / ResumeWorkflowResult commands: resolve, then stream.
            if (_ctx.TryResolvePromptCommand is not null && _ctx.StreamCommandPrompt is not null)
            {
                OneCode.App.Services.CommandDispatchResult? dispatchResult = null;
                try
                {
                    dispatchResult = await _ctx.TryResolvePromptCommand(text, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Invoke(() => _shell.Transcript.AddError(ex.Message));
                    return;
                }

                switch (dispatchResult)
                {
                    case OneCode.App.Services.CommandDispatchResult.Prompt pi:
                        await RunCommandPromptAsync(text, pi.Content, pi.AllowedTools, ct).ConfigureAwait(false);
                        if (_ctx.IsExitRequested?.Invoke() == true)
                            OnQuitRequested();
                        return;

                    case OneCode.App.Services.CommandDispatchResult.ResumeWorkflow rw
                        when _ctx.StreamResumeWorkflow is not null:
                        await RunResumeWorkflowAsync(rw.SessionId, rw.Kind, ct).ConfigureAwait(false);
                        return;
                }
            }

            // All other commands → local handler.
            // User message is already shown by OnUserSubmitted (AddUserMessageDirect).
            var progressLabel = GetProgressLabel(text);
            Invoke(() => { _shell.ChatInput.SetBusy(true); _shell.Transcript.BeginStreaming(); _shell.SetAgentBusy(true, progressLabel); });
            string? result;
            try
            {
                result = await _ctx.ExecuteCommand(text, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Invoke(() => _shell.Transcript.AddError(ex.Message));
                return;
            }
            finally
            {
                Invoke(() => { _shell.Transcript.EndStreaming(); _shell.ChatInput.SetBusy(false); _shell.SetAgentBusy(false); _shell.FocusChatInput(); });
            }

            if (result is not null)
            {
                Invoke(() =>
                {
                    _shell.Transcript.AddCommandResult(result);
                    RefreshSessionName();
                });

                if (_ctx.IsExitRequested?.Invoke() == true)
                {
                    OnQuitRequested();
                }
                return;
            }

            // Unknown slash command → fall through to AI (AI can answer meta questions).
        }

        await RunQueryAsync(text, images, ct);
    }
}
