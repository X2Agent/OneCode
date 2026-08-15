namespace OneCode.App.Tui;

/// <summary>
/// Background query streaming for <see cref="OneCodeToplevel"/>.
/// Hosts <see cref="RunQueryAsync"/> and <see cref="RunCommandPromptAsync"/>,
/// which stream TuiEvents from the backend and forward them to <see cref="DispatchEvent"/>.
/// </summary>
public sealed partial class OneCodeToplevel
{
    // Query loop (background task, UI updates via Invoke)

    /// <summary>
    /// Streams a command prompt (from PromptResult commands like /review) through
    /// the normal TuiEvent pipeline. Shows spinner, tool calls, and streaming text
    /// — the same UX as a regular user query, but with the prompt content supplied
    /// by the command rather than typed by the user.
    /// </summary>
    private async Task RunCommandPromptAsync(string userText, string prompt, string[]? allowedTools, CancellationToken ct)
    {
        try
        {
            await _ctx.CreateSession(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Invoke(() => _shell.Transcript.AddError($"Session error: {ex.Message}"));
            return;
        }

        Invoke(() =>
        {
            RefreshSessionName();
            // Prefer OnUserSubmitted / DrainInputQueueAsync for the user bubble.
            // This path only fills in when the message was not shown yet.
            if (!_userMessageShown)
                _shell.Transcript.AddUserMessage(userText);
            _shell.Transcript.BeginStreaming();
            _shell.ChatInput.SetBusy(true);
            _shell.SetAgentBusy(true);
        });

        try
        {
            await foreach (var evt in _ctx.StreamCommandPrompt!(prompt, allowedTools, ct).ConfigureAwait(false))
            {
                var snapshot = evt;
                Invoke(() => DispatchEvent(snapshot));
            }
        }
        catch (OperationCanceledException)
        {
            Invoke(() =>
            {
                _shell.Transcript.EndStreaming();
                _shell.Transcript.AddSystem("(cancelled)");
            });
        }
        catch (Exception ex)
        {
            Invoke(() =>
            {
                _shell.Transcript.EndStreaming();
                _shell.Transcript.AddError(ex.Message);
            });
        }
        finally
        {
            Invoke(() =>
            {
                _shell.Transcript.EndStreaming();
                _shell.ChatInput.SetBusy(false);
                _shell.SetAgentBusy(false);
                _shell.FocusChatInput();
            });
        }
    }

    /// <summary>
    /// Resumes a durable workflow (Goal/Team) from a checkpoint.
    /// Routes directly to the workflow resume stream without passing through the LLM pipeline.
    /// </summary>
    private async Task RunResumeWorkflowAsync(
        string sessionId, OneCode.Core.Commands.WorkflowResumeKind kind, CancellationToken ct)
    {
        Invoke(() =>
        {
            _shell.Transcript.BeginStreaming();
            _shell.ChatInput.SetBusy(true);
            _shell.SetAgentBusy(true, $"恢复 {kind} 工作流…");
        });

        try
        {
            await foreach (var evt in _ctx.StreamResumeWorkflow!(sessionId, kind, ct).ConfigureAwait(false))
            {
                var snapshot = evt;
                Invoke(() => DispatchEvent(snapshot));
            }
        }
        catch (OperationCanceledException)
        {
            Invoke(() =>
            {
                _shell.Transcript.EndStreaming();
                _shell.Transcript.AddSystem("(cancelled)");
            });
        }
        catch (Exception ex)
        {
            Invoke(() =>
            {
                _shell.Transcript.EndStreaming();
                _shell.Transcript.AddError(ex.Message);
            });
        }
        finally
        {
            Invoke(() =>
            {
                _shell.Transcript.EndStreaming();
                _shell.ChatInput.SetBusy(false);
                _shell.SetAgentBusy(false);
                _shell.FocusChatInput();
            });
        }
    }

    private async Task RunQueryAsync(string userText, IReadOnlyList<string>? imagePaths, CancellationToken ct)
    {
        try
        {
            await _ctx.CreateSession(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Invoke(() => _shell.Transcript.AddError($"Session error: {ex.Message}"));
            return;
        }

        Invoke(() =>
        {
            RefreshSessionName();
            // Prefer OnUserSubmitted / DrainInputQueueAsync for the user bubble.
            // This path only fills in when the message was not shown yet.
            if (!_userMessageShown)
                _shell.Transcript.AddUserMessage(userText);
            _shell.Transcript.BeginStreaming();
            _shell.ChatInput.SetBusy(true);
            _shell.SetAgentBusy(true);
        });

        try
        {
            await foreach (var evt in _ctx.StreamQuery(userText, imagePaths, ct).ConfigureAwait(false))
            {
                var snapshot = evt;
                Invoke(() => DispatchEvent(snapshot));
            }
        }
        catch (OperationCanceledException)
        {
            Invoke(() =>
            {
                _shell.Transcript.EndStreaming();
                _shell.Transcript.AddSystem("(cancelled)");
            });
        }
        catch (Exception ex)
        {
            Invoke(() =>
            {
                _shell.Transcript.EndStreaming();
                _shell.Transcript.AddError(ex.Message);
            });
        }
        finally
        {
            Invoke(() =>
            {
                _shell.Transcript.EndStreaming();
                _shell.ChatInput.SetBusy(false);
                _shell.SetAgentBusy(false);
                _shell.FocusChatInput();
            });
        }
    }
}
