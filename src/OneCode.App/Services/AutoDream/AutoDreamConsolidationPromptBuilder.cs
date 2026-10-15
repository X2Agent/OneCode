using System.Text;
using OneCode.Core.Prompt;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.AutoDream;

/// <summary>
/// AutoDream 整合提示词构建器：收集上次整合以来写入的 working-memory 快照与当前内存会话中的最新对话轮次，
/// 渲染 <c>system/autodream-consolidation</c> 模板变量，产出整合 Agent 的完整提示词。
/// </summary>
internal sealed class AutoDreamConsolidationPromptBuilder(
    IPromptManager promptManager,
    AutoDreamWorkingMemoryScanner workingMemoryScanner,
    Func<string?> projectRootProvider,
    Func<Conversation?>? currentConversationProvider = null)
{
    private readonly IPromptManager _promptManager = promptManager;
    private readonly AutoDreamWorkingMemoryScanner _workingMemoryScanner = workingMemoryScanner;
    private readonly Func<string?> _projectRootProvider = projectRootProvider;
    private readonly Func<Conversation?>? _currentConversationProvider = currentConversationProvider;

    /// <summary>
    /// Upper bound on working-memory artefacts included in one consolidation prompt.
    /// Working notes are unbounded in principle; the prompt is not.
    /// </summary>
    private const int MaxWorkingMemoryArtefacts = 20;

    /// <summary>单条对话轮次预览的最大字符数，防止长篇回复撑大整合提示词。</summary>
    private const int MaxTurnPreviewChars = 800;

    /// <summary>注入整合提示词的当前对话轮次摘要最大总字符数。</summary>
    private const int MaxCurrentConversationChars = 12_000;

    /// <summary>当前内存会话是否存在自 <paramref name="since"/> 以来的新对话轮次。</summary>
    public bool HasCurrentConversationDelta(DateTimeOffset since)
    {
        var conversation = GetMatchingCurrentConversation();
        if (conversation is null || conversation.Messages.Count == 0)
            return false;

        foreach (var message in conversation.Messages)
        {
            if (message.Timestamp <= since)
                continue;

            switch (message)
            {
                case UserMessage { IsMeta: false } user when !string.IsNullOrWhiteSpace(user.Content):
                case AssistantMessage assistant when assistant.Content.OfType<TextBlock>().Any(b => !string.IsNullOrWhiteSpace(b.Text)):
                    return true;
            }
        }

        return false;
    }

    /// <summary>提取当前内存会话自 <paramref name="since"/> 以来的用户与助手对话轮次摘要。</summary>
    internal IReadOnlyList<string> CollectCurrentConversationTurns(DateTimeOffset since, bool includeAllTurns = false)
    {
        var conversation = GetMatchingCurrentConversation();
        if (conversation is null || conversation.Messages.Count == 0)
            return [];

        var candidateMessages = includeAllTurns
            ? conversation.Messages
            : conversation.Messages.Where(m => m.Timestamp > since).ToList();

        if (candidateMessages.Count == 0 && includeAllTurns)
            candidateMessages = conversation.Messages;

        List<string> allTurns = [];
        foreach (var message in candidateMessages)
        {
            switch (message)
            {
                case UserMessage { IsMeta: false } user when !string.IsNullOrWhiteSpace(user.Content):
                    allTurns.Add($"User: {TruncateSingleLine(user.Content, MaxTurnPreviewChars)}");
                    break;
                case AssistantMessage assistant:
                {
                    var text = string.Join(" ", assistant.Content
                        .OfType<TextBlock>()
                        .Select(b => b.Text)
                        .Where(t => !string.IsNullOrWhiteSpace(t)));
                    if (!string.IsNullOrWhiteSpace(text))
                        allTurns.Add($"Assistant: {TruncateSingleLine(text, MaxTurnPreviewChars)}");
                    break;
                }
            }
        }

        if (allTurns.Count == 0)
            return [];

        // 从最新轮次向前截取，确保刚刚完成的任务轮次优先保留在预算内
        List<string> selectedReversed = [];
        var remaining = MaxCurrentConversationChars;
        for (var i = allTurns.Count - 1; i >= 0; i--)
        {
            var turn = allTurns[i];
            if (turn.Length + 3 > remaining)
                break;

            selectedReversed.Add(turn);
            remaining -= turn.Length + 3;
        }

        selectedReversed.Reverse();
        return selectedReversed;
    }

    /// <summary>构建整合提示词：注入自 <paramref name="since"/> 以来的 working-memory 快照与当前内存对话轮次摘要。</summary>
    public async Task<string> BuildConsolidationPromptAsync(
        DateTimeOffset since,
        bool includeAllTurns,
        CancellationToken ct)
    {
        var projectRoot = _projectRootProvider() ?? "(unknown)";
        var artefacts = _workingMemoryScanner.Collect(projectRoot, since, MaxWorkingMemoryArtefacts, ct);
        var currentTurns = CollectCurrentConversationTurns(since, includeAllTurns);

        var variables = new Dictionary<string, string>
        {
            ["since"] = since == DateTimeOffset.MinValue
                ? "never"
                : since.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["project_root"] = projectRoot,
            ["working_memory_root"] = FileMemoryStorePaths.ResolveRoot(projectRoot),
            ["working_memory_section"] = FormatWorkingMemorySection(artefacts),
            ["current_conversation_section"] = FormatCurrentConversationSection(currentTurns),
        };
        return await _promptManager.RenderPromptAsync("system/autodream-consolidation", variables, ct)
            .ConfigureAwait(false);
    }

    internal static string FormatCurrentConversationSection(IReadOnlyList<string> turns)
    {
        if (turns.Count == 0)
            return "No new user/assistant turns were recorded in the active conversation since the last consolidation.";

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"{turns.Count} conversation turn(s) from the active session:");

        foreach (var turn in turns)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {turn}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Renders the snapshot as prompt text. The path and timestamp are included so every candidate can
    /// be attributed back to the revision it came from.
    /// </summary>
    private static string FormatWorkingMemorySection(IReadOnlyList<WorkingMemoryArtefact> artefacts)
    {
        if (artefacts.Count == 0)
        {
            return "No working-memory artefacts were written since the last consolidation. " +
                   "Fall back to reviewing the current conversation turns below.";
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"{artefacts.Count} artefact(s) were written since the last consolidation:");

        foreach (var artefact in artefacts)
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"### {artefact.RelativePath} (modified {artefact.LastModifiedAt:yyyy-MM-dd HH:mm:ss}Z)");
            sb.AppendLine(artefact.Content.TrimEnd());
        }

        return sb.ToString().TrimEnd();
    }

    private Conversation? GetMatchingCurrentConversation()
    {
        var conversation = _currentConversationProvider?.Invoke();
        if (conversation is null)
            return null;

        var projectRoot = _projectRootProvider();
        if (string.IsNullOrWhiteSpace(projectRoot) || string.IsNullOrWhiteSpace(conversation.WorkingDirectory))
            return conversation;

        return PathsMatch(conversation.WorkingDirectory, projectRoot)
            ? conversation
            : null;
    }

    private static bool PathsMatch(string left, string right)
    {
        var normalizedLeft = NormalizePath(left);
        var normalizedRight = NormalizePath(right);
        return string.Equals(normalizedLeft, normalizedRight,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static string TruncateSingleLine(string text, int maxChars)
    {
        var collapsed = string.Join(" ", text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return collapsed.Length <= maxChars
            ? collapsed
            : string.Concat(collapsed.AsSpan(0, maxChars), "...");
    }
}
