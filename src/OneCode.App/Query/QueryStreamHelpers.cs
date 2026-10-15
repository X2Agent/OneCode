using Microsoft.Extensions.AI;
using OneCode.App.Services.Agent;
using OneCode.Infrastructure.Middleware;

namespace OneCode.App.Query;

/// <summary>QueryStreamEngine 的无状态纯函数（用量聚合 + 终因解析）。</summary>
internal static class QueryStreamHelpers
{
    /// <summary>聚合跨纠偏轮的 TokenUsage（左侧为 null 时直接返回右侧）。</summary>
    public static TokenUsage SumUsage(TokenUsage? left, TokenUsage right) =>
        left is null
            ? right
            : new TokenUsage(
                left.InputTokens + right.InputTokens,
                left.OutputTokens + right.OutputTokens,
                left.CacheReadTokens + right.CacheReadTokens,
                left.CacheWriteTokens + right.CacheWriteTokens);

    public static TerminalOutcomeState ResolveTerminalOutcome(
        StreamingSession session,
        MainAgentRunOptions options,
        MainAgentRunResult? runResult,
        string finalText)
    {
        // Compute real terminal reason: combine runner result with turn-limit detection.
        var outcome = new TerminalOutcomeState
        {
            Reason = runResult?.TerminalReason ?? RunTerminalReason.Completed,
            TransactionRolledBack = runResult?.TransactionRolledBack ?? false,
            ValidationFailureSummary = runResult?.ValidationFailureSummary,
        };

        // If the agent didn't explicitly signal a terminal reason, check turn limit.
        if (outcome.Reason == RunTerminalReason.Completed && session.TurnCount >= options.MaxTurns)
            outcome.Reason = RunTerminalReason.TurnLimitReached;

        // Detect budget exceeded from final text (BudgetGuard middleware short-circuits with a text marker).
        if (outcome.Reason == RunTerminalReason.Completed
            && finalText.Contains("[Budget Exceeded]", StringComparison.OrdinalIgnoreCase))
        {
            outcome.Reason = RunTerminalReason.BudgetExceeded;
        }

        return outcome;
    }

    /// <summary>
    /// Builds a user <see cref="ChatMessage"/>. When <paramref name="imagePaths"/> is provided,
    /// constructs a multi-content message with text + image <see cref="DataContent"/> blocks
    /// following the MAF multimodal pattern.
    /// </summary>
    public static ChatMessage BuildUserMessage(
        string prompt,
        IReadOnlyList<string>? imagePaths,
        ILogger logger)
    {
        if (imagePaths is not { Count: > 0 })
            return new ChatMessage(ChatRole.User, prompt);

        List<AIContent> contents = [];
        if (!string.IsNullOrEmpty(prompt))
            contents.Add(new TextContent(prompt));

        foreach (var path in imagePaths)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var mediaType = ext switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    ".bmp" => "image/bmp",
                    _ => "image/png",
                };
                contents.Add(new DataContent(bytes, mediaType));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read image {Path}", path);
                contents.Add(new TextContent($"[Failed to load image: {Path.GetFileName(path)}]"));
            }
        }

        return new ChatMessage(ChatRole.User, contents);
    }

    public static string PromptTooLongHint(Exception runException) =>
        PromptTooLongDetector.IsPromptTooLong(runException)
            ? " —— 上下文超出模型窗口，请执行 /compact 压缩会话历史后重试。"
            : string.Empty;

    public static MainAgentRunOptions BuildAgentRunOptions(
        QueryStreamRequest request,
        ToolAssembler toolAssembler)
        => new()
        {
            ModelId = request.ModelId,
            SystemPrompt = request.SystemPrompt,
            HarnessInstructions = request.HarnessInstructions,
            UserPrompt = request.UserPrompt,
            UserMessage = request.IsMultimodal ? request.LastUserMessage : null,
            Messages = request.RunInputMessages,
            WorkingDirectory = request.WorkingDirectory,
            MaxTurns = toolAssembler.ResolveMaxTurns(),
            EnableThinking = request.ThinkingBudget > 0,
            ThinkingBudgetTokens = request.ThinkingBudget ?? 0,
            Tools = request.LocalTools.Cast<AITool>().ToList(),
            ToolCapabilities = request.ToolCapabilities,
            WorkingMode = request.WorkingMode,
            FileChangeCallback = request.FileChangeCallback,
            ConversationId = request.ConversationId,
            AgentRunId = request.AgentRunId,
            MaxBudgetTokens = toolAssembler.ResolveMaxBudgetTokens(),
            HistorySeedBoundary = request.HistorySeedBoundary,
            SeedTodoTasks = request.PrescribedBuildPlan?.Tasks,
        };

    public static CacheSafeParams CreateCacheSafeParams(
        QueryStreamRequest request,
        IReadOnlyList<AIFunction> localTools)
    {
        var toolList = localTools.Cast<AITool>().ToList();
        return new CacheSafeParams
        {
            SystemPrompt = request.SystemPrompt,
            ModelId = request.ModelId,
            ThinkingBudget = request.ThinkingBudget,
            Tools = toolList.Count > 0 ? toolList : null,
            ToolCapabilities = request.ToolCapabilities,
            Metadata = new Dictionary<string, object?> { ["turn"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
        };
    }
}
