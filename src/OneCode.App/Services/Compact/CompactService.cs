using Microsoft.Extensions.AI;
using OneCode.App.Session;
using OneCode.Core;
using OneCode.Core.Models;

namespace OneCode.App.Services.Compact;

/// <summary>
/// App 层显式压缩服务——通过 /compact 命令触发（用户主动深度压缩）。
///
/// <para><b>与 MAF CompactionProvider 的分工</b>：
/// <list type="bullet">
///   <item><description><b>MAF CompactionProvider</b>（Infrastructure 层）：in-pipeline 自动压缩，
///   每次 agent 调用前按 token 预算自动执行 4 层策略（L0 去重→L1 ToolResult 折叠→L2 LLM 摘要→L3 截断），
///   <b>不修改持久化的消息历史</b>，只压缩发给模型的消息。用户无感知。</description></item>
///   <item><description><b>本类</b>（App 层）：用户显式 /compact 命令触发的深度压缩——
///   发送完整对话历史给模型生成摘要，然后<b>替换持久化消息历史</b>为压缩边界标记 + 摘要内容。
///   适用于用户主动管理长对话上下文。MAF 没有"替换持久化历史"和"Pre/PostCompact hooks"能力，
///   本类补充这些项目特有需求。</description></item>
/// </list>
/// </para>
///
/// <para><b>职责</b>：thin orchestrator，具体逻辑委派给 <see cref="CompactPromptBuilder"/>、
/// <see cref="CompactApplier"/>。PreCompact/PostCompact hook 调度内联在本类中。
/// <see cref="GetBudgetStatus"/> 同时供 <see cref="AutoCompactService"/> 用于 70% 告警检查。</para>
/// </summary>
public sealed class CompactService(
    IChatClient chatClient,
    ILogger<CompactService> logger,
    IHookExecutionService hooks,
    CompactSessionDependencies session,
    CompactPromptBuilder promptBuilder,
    CompactApplier applier)
{
    private ISessionConversationAccess sessionAccess => session.SessionAccess;
    private ISessionManager sessionManager => session.SessionManager;
    private IModelManager modelManager => session.ModelManager;
    private ITokenEstimator tokenEstimator => session.TokenEstimator;
    /// <summary>
    /// 获取会话的 token 预算状态。注入 ModelManager 以读取用户配置的 ContextWindow。
    /// </summary>
    public TokenBudgetStatus GetBudgetStatus(Conversation session, string? systemPrompt = null) =>
        TokenBudget.Estimate(session, tokenEstimator, systemPrompt, modelManager);

    /// <summary>
    /// Compact a conversation (foreground session if <paramref name="session"/> is null).
    /// </summary>
    public async Task<string?> CompactAsync(
        Conversation? session = null,
        string? customInstructions = null,
        int? fromMessageIndex = null,
        int? upToMessageIndex = null,
        CancellationToken ct = default,
        IProgress<CompactProgress>? progress = null)
    {
        session ??= sessionAccess.ForegroundConversation;
        if (session == null)
        {
            logger.LogWarning("CompactAsync: no active conversation");
            return null;
        }

        progress?.Report(new CompactProgress("Inspecting conversation", 10));

        // PreCompact hooks (TS: executePreCompactHooks)
        progress?.Report(new CompactProgress("Running PreCompact hooks", 12));
        await FirePreCompactAsync(session, ct).ConfigureAwait(false);

        var messages = session.Messages;

        var significantMessages = messages
            .Where(m => m is UserMessage or AssistantMessage)
            .ToList();

        if (significantMessages.Count < CompactConstants.MinSignificantMessagesForCompact)
        {
            logger.LogInformation("Not enough messages to compact ({Count})", significantMessages.Count);
            progress?.Report(new CompactProgress("Not enough messages to compact", 100));
            return null;
        }

        var isPartial = fromMessageIndex.HasValue || upToMessageIndex.HasValue;

        logger.LogInformation(
            "Compacting conversation '{Name}' ({Count} messages, partial={Partial})",
            session.Name, messages.Count, isPartial);

        var systemPrompt = await promptBuilder.BuildSystemPromptAsync(customInstructions, ct).ConfigureAwait(false);
        progress?.Report(new CompactProgress("Preparing summary request", 25));

        IReadOnlyList<Message> messagesToCompact;
        if (isPartial)
        {
            var from = Math.Max(0, fromMessageIndex ?? 0);
            var to = Math.Min(messages.Count, upToMessageIndex ?? messages.Count);
            messagesToCompact = messages.Skip(from).Take(to - from).ToList();
        }
        else
        {
            messagesToCompact = messages.ToList();
        }

        var chatRequest = promptBuilder.BuildChatRequest(messagesToCompact, systemPrompt, session.Model);

        ChatResponse response;
        try
        {
            progress?.Report(new CompactProgress("Requesting summary from model", 55));
            response = await chatClient.GetResponseAsync(chatRequest.ChatMessages, chatRequest.Options, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Compact API call failed");
            throw;
        }

        var rawSummary = response.Text;

        if (string.IsNullOrWhiteSpace(rawSummary))
        {
            logger.LogWarning("Compact returned empty summary");
            return null;
        }

        var formattedSummary = CompactPromptBuilder.FormatSummary(rawSummary);
        progress?.Report(new CompactProgress("Applying compacted summary", 80));

        if (isPartial)
        {
            applier.ApplyPartialCompact(session, formattedSummary, fromMessageIndex ?? 0, upToMessageIndex ?? messages.Count);
        }
        else
        {
            applier.ApplyFullCompact(session, formattedSummary);
        }

        session.Metadata["lastCompactedAt"] = DateTimeOffset.UtcNow.ToString("O");
        session.Metadata["lastCompactedMessageCount"] = session.Messages.Count;

        await sessionManager.SaveAsync(ct);
        progress?.Report(new CompactProgress("Saving compacted conversation", 95));

        await FirePostCompactAsync(session, formattedSummary, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Compact complete: {Before} messages → {After} messages",
            messages.Count, session.Messages.Count);

        progress?.Report(new CompactProgress("Compaction complete", 100));

        return formattedSummary;
    }

    /// <summary>
    /// Fires the <see cref="HookEvent.PreCompact"/> hook.
    /// </summary>
    private async Task FirePreCompactAsync(Conversation session, CancellationToken ct)
    {
        await hooks.FireAsync(new HookPayload
        {
            Event = HookEvent.PreCompact,
            SessionId = session.Id,
            Cwd = session.WorkingDirectory,
        }, ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fires the <see cref="HookEvent.PostCompact"/> hook with the produced summary.
    /// </summary>
    private async Task FirePostCompactAsync(Conversation session, string summary, CancellationToken ct)
    {
        await hooks.FireAsync(new HookPayload
        {
            Event = HookEvent.PostCompact,
            SessionId = session.Id,
            ToolResponse = summary,
        }, ct: ct).ConfigureAwait(false);
    }
}

public sealed record CompactProgress(string Message, double Percent);
