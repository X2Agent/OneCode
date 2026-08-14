using OneCode.App.Services.Compact;
using OneCode.App.Tui;
using OneCode.Core.Models;
using OneCode.Infrastructure.Config;
using System.Runtime.CompilerServices;

namespace OneCode.App.Services.Streaming;

/// <summary>
/// Orchestrates the TUI query streaming pipeline: dispatches user input to
/// Normal / GOAL / TEAM streams based on <see cref="WorkingMode"/>, and streams
/// command-produced prompts (e.g. <c>/review</c>) through the normal TuiEvent
/// pipeline so the user sees the same UX as a regular query.
/// </summary>
/// <remarks>
/// Extracted from <see cref="InteractiveModeExecutor"/>. GOAL/TEAM streaming is
/// delegated to <see cref="OrchestrationStreamService"/>; Normal and
/// command-prompt streaming talk to <see cref="InteractiveSession.ConversationRunner"/>
/// directly while draining file-change events and generating next-prompt
/// suggestions.
/// </remarks>
public sealed class QueryStreamService(
    QueryRuntimeDependencies runtime,
    QueryOrchestrationDependencies orchestration,
    ReviewCacheService reviewCacheService,
    ILogger<QueryStreamService> logger)
{
    private IModelManager modelManager => runtime.ModelManager;
    private IAppStateAccessor appStateAccessor => runtime.AppState;
    private IConfigManager configManager => runtime.ConfigManager;
    private ThinkingParamsResolver thinkingParamsResolver => runtime.ThinkingParams;
    private OrchestrationStreamService orchestrationStreamService => orchestration.OrchestrationStream;
    private AutoCompactService autoCompactService => orchestration.AutoCompact;
    private OneCode.Core.Coordinator.ITeamOrchestrationService teamOrchestrationService => orchestration.TeamOrchestration;
    /// <summary>
    /// Main streaming entry point: logs the user message, resolves the current
    /// model, and dispatches to Normal/Goal/Team streams based on WorkingMode.
    /// </summary>
    public async IAsyncEnumerable<TuiEvent> StreamQueryAsync(
        InteractiveSession session,
        string text,
        IReadOnlyList<string>? imagePaths,
        [EnumeratorCancellation] CancellationToken ct)
    {
        logger.LogInformation("User input received ({Length} chars)", text.Length);

        if (BuildConfigMissingMessage(configManager) is { } configError)
        {
            yield return new TuiError(configError);
            yield break;
        }

        var currentModelId = modelManager.GetMainModel(appStateAccessor.Current.MainLoopModel).Id;
        var conversationId = session.SessionManager.ForegroundConversation?.Id;

        // GOAL mode dispatch
        if (session.ModeController.Mode == WorkingMode.Goal)
        {
            await foreach (var evt in orchestrationStreamService.StreamGoalAsync(session, text, currentModelId, imagePaths, ct).ConfigureAwait(false))
                yield return evt;
            await foreach (var evt in EmitAutoCompactIfNeededAsync(
                conversationId is { } goalConversationId
                    ? session.SessionManager.GetConversation(goalConversationId)
                    : null,
                session.SystemPrompt,
                ct).ConfigureAwait(false))
                yield return evt;
            yield break;
        }

        // TEAM mode dispatch (explicit error if no teams registered)
        if (session.ModeController.Mode == WorkingMode.Team)
        {
            if (teamOrchestrationService is not { } teamService || teamService.RegisteredTeams.Count == 0)
            {
                logger.LogWarning("TEAM mode active but no teams registered; refusing to fall back to Normal");
                yield return new TuiError("TEAM mode is active but no teams are registered. Use /team register to add a team, or switch to BUILD mode.");
                yield break;
            }

            await foreach (var evt in orchestrationStreamService.StreamTeamAsync(session, teamService, text, imagePaths, ct).ConfigureAwait(false))
                yield return evt;
            await foreach (var evt in EmitAutoCompactIfNeededAsync(
                conversationId is { } teamConversationId
                    ? session.SessionManager.GetConversation(teamConversationId)
                    : null,
                session.SystemPrompt,
                ct).ConfigureAwait(false))
                yield return evt;
            yield break;
        }

        // Normal mode (Build / Plan)
        await foreach (var evt in StreamNormalAsync(session, text, imagePaths, currentModelId, ct).ConfigureAwait(false))
            yield return evt;
    }

    /// <summary>
    /// Normal mode streaming: ChatService.StreamQueryAsync with file-change
    /// queue draining and next-prompt suggestion generation.
    /// </summary>
    public async IAsyncEnumerable<TuiEvent> StreamNormalAsync(
        InteractiveSession session,
        string text,
        IReadOnlyList<string>? imagePaths,
        string currentModelId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in StreamChatCoreAsync(
            session, text, session.SystemPrompt, currentModelId, imagePaths, ct).ConfigureAwait(false))
            yield return evt;
    }

    /// <summary>
    /// Streams a command-produced prompt (e.g. /review) through the normal
    /// TuiEvent pipeline so the user sees spinner, tool calls, and streaming
    /// text — same UX as a regular query.
    /// </summary>
    public async IAsyncEnumerable<TuiEvent> StreamCommandPromptAsync(
        InteractiveSession session,
        string prompt,
        string[]? allowedTools,
        [EnumeratorCancellation] CancellationToken ct)
    {
        logger.LogInformation("Command prompt streaming ({Length} chars)", prompt.Length);

        if (BuildConfigMissingMessage(configManager) is { } configError)
        {
            yield return new TuiError(configError);
            yield break;
        }

        var currentModelId = modelManager.GetMainModel(appStateAccessor.Current.MainLoopModel).Id;

        // yield is allowed in try/finally but not try/catch (CS1626).
        // Commit only when the core stream completes normally; otherwise discard.
        var succeeded = false;
        try
        {
            await foreach (var evt in StreamCommandPromptCoreAsync(
                session, prompt, allowedTools, currentModelId, ct).ConfigureAwait(false))
            {
                yield return evt;
            }

            succeeded = true;
        }
        finally
        {
            if (succeeded)
                reviewCacheService.CommitPending();
            else
                reviewCacheService.DiscardPending();
        }
    }

    private async IAsyncEnumerable<TuiEvent> StreamCommandPromptCoreAsync(
        InteractiveSession session,
        string prompt,
        string[]? allowedTools,
        string currentModelId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var augmentedSystemPrompt = allowedTools is { Length: > 0 }
            ? session.SystemPrompt + $"\n\n[Command restriction] Only the following tools may be used: {string.Join(", ", allowedTools)}"
            : session.SystemPrompt;

        await foreach (var evt in StreamChatCoreAsync(
            session, prompt, augmentedSystemPrompt, currentModelId, imagePaths: null, ct).ConfigureAwait(false))
            yield return evt;
    }

    /// <summary>
    /// Resumes a durable workflow (Goal/Team) from a checkpoint.
    /// Called directly by the TUI dispatch layer when <see cref="CommandResult.ResumeWorkflowResult"/>
    /// is returned by a command, bypassing the LLM query pipeline entirely.
    /// </summary>
    public async IAsyncEnumerable<TuiEvent> StreamResumeWorkflowAsync(
        InteractiveSession session,
        string sessionId,
        WorkflowResumeKind kind,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (BuildConfigMissingMessage(configManager) is { } configError)
        {
            yield return new TuiError(configError);
            yield break;
        }

        var currentModelId = modelManager.GetMainModel(appStateAccessor.Current.MainLoopModel).Id;

        switch (kind)
        {
            case WorkflowResumeKind.Goal:
                await foreach (var evt in orchestrationStreamService.StreamResumeGoalAsync(
                    sessionId, currentModelId, session.SystemPrompt, ct).ConfigureAwait(false))
                    yield return evt;
                break;

            case WorkflowResumeKind.Team:
                if (teamOrchestrationService is { } teamService)
                {
                    await foreach (var evt in orchestrationStreamService.StreamResumeTeamAsync(
                        teamService, sessionId, ct).ConfigureAwait(false))
                        yield return evt;
                }
                else
                {
                    yield return new TuiError("Team mode is not available for resume.");
                }
                break;
        }
    }

    /// <summary>
    /// Shared ChatService streaming loop: thinking params, file-change drain, auto-compact.
    /// </summary>
    private async IAsyncEnumerable<TuiEvent> StreamChatCoreAsync(
        InteractiveSession session,
        string prompt,
        string systemPrompt,
        string currentModelId,
        IReadOnlyList<string>? imagePaths,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var (thinkingEnabled, thinkingBudget) = thinkingParamsResolver?.Resolve(appStateAccessor, currentModelId)
            ?? (false, 0);
        var conversationId = session.SessionManager.ForegroundConversation?.Id;

        var fileChangeQueue = new System.Collections.Concurrent.ConcurrentQueue<FileChange>();
        Action<FileChange> fileChangeCallback = fc => fileChangeQueue.Enqueue(fc);

        await foreach (var evt in session.ConversationRunner.StreamQueryAsync(
            prompt, systemPrompt, currentModelId,
            thinkingBudget: thinkingEnabled ? thinkingBudget : null,
            ct: ct,
            workingMode: session.ModeController.Mode,
            fileChangeCallback: fileChangeCallback,
            imagePaths: imagePaths).ConfigureAwait(false))
        {
            if (TuiEventMapper.MapQueryEventToTuiEvent(evt) is { } mapped)
                yield return mapped;

            while (fileChangeQueue.TryDequeue(out var fc))
                yield return new TuiFileChange(fc.FileName, fc.AddedLines, fc.RemovedLines);
        }

        while (fileChangeQueue.TryDequeue(out var fc))
            yield return new TuiFileChange(fc.FileName, fc.AddedLines, fc.RemovedLines);

        await foreach (var evt in EmitAutoCompactIfNeededAsync(
            conversationId is { } id
                ? session.SessionManager.GetConversation(id)
                : null,
            systemPrompt,
            ct).ConfigureAwait(false))
            yield return evt;
    }

    /// <summary>
    /// 检查关键配置项（model、apiKey）是否已配置。
    /// 全部就绪返回 null；缺少任一项返回用户友好的提示消息。
    /// </summary>
    internal static string? BuildConfigMissingMessage(IConfigManager configManager)
    {
        var settings = configManager.Current.Effective;
        var missing = new List<string>();

        if (string.IsNullOrEmpty(settings.Model))
            missing.Add("model（模型）");
        if (string.IsNullOrEmpty(settings.ApiKey))
            missing.Add("apiKey（API 密钥）");

        if (missing.Count == 0)
            return null;

        return $"缺少必要配置：{string.Join("、", missing)}。请通过以下方式配置：\n" +
               "  1. 运行 /model <模型ID>（如 /model claude-sonnet-4-6）\n" +
               "  2. 运行 /config 打开配置面板\n" +
               "  3. 在 settings.json 中设置对应字段";
    }

    /// <summary>
    /// Shared post-turn auto-compaction check for all streaming paths (Normal/Goal/Team/Command).
    /// Yields a <see cref="TuiCompactSuggested"/> event when token usage crosses 70%.
    ///
    /// Actual compaction runs inside the MAF CompactionProvider pipeline; this method
    /// only surfaces the 70% warning.
    /// </summary>
    private async IAsyncEnumerable<TuiEvent> EmitAutoCompactIfNeededAsync(
        OneCode.Core.Domain.Conversation? conversation,
        string? systemPrompt,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (conversation is null)
            yield break;

        await autoCompactService.CheckAndWarnAsync(conversation, systemPrompt, ct)
            .ConfigureAwait(false);

        if (autoCompactService.ConsumeWarning(conversation.Id))
        {
            yield return new TuiCompactSuggested(
                "Context is at 70%. Consider running /compact to summarize the conversation.");
        }
    }
}
