using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using OneCode.Core.Prompt;

namespace OneCode.App.Services.Compact;

/// <summary>
/// Builds the system prompt, chat messages, and chat options used to invoke the
/// summarisation model during a compact run.
///
/// Extracted from <see cref="CompactService"/> to keep prompt construction concerns
/// in one place and out of the orchestration flow.
/// </summary>
public sealed partial class CompactPromptBuilder
{
    /// <summary>内置兜底——IPromptManager 无文件时仍可压缩（测试与缺文件启动）。</summary>
    public const string FallbackCompactPrompt =
        "Summarize the conversation so far into a concise briefing for continuing work. "
        + "Preserve decisions, file paths, errors, and next steps. "
        + "Respond with <analysis>...</analysis> then <summary>...</summary>.";

    // 预编译正则——消除 FormatSummary 中重复编译开销。
    // pattern 固定（非用户输入），无 ReDoS 风险，但 [GeneratedRegex] 在编译时生成源码，
    // 避免每次调用 Regex.Replace/Match 时重新编译正则树。
    [GeneratedRegex(@"<analysis>[\s\S]*?</analysis>", RegexOptions.IgnoreCase)]
    private static partial Regex AnalysisBlockRegex();

    // 带捕获组——Match 用于提取 <summary> 内文，Replace 用于整块替换（捕获组不影响 Replace 语义）。
    [GeneratedRegex(@"<summary>([\s\S]*?)</summary>", RegexOptions.IgnoreCase)]
    private static partial Regex SummaryBlockRegex();

    private readonly IPromptManager _promptManager;

    public CompactPromptBuilder(IPromptManager promptManager)
    {
        _promptManager = promptManager;
    }

    /// <summary>
    /// 加载压缩摘要 prompt（<c>system/compact</c>）。文件缺失时返回内置兜底，供
    /// 显式 <c>/compact</c> 与 MAF in-pipeline 压缩共用，避免两套 fallback 行为漂移。
    /// </summary>
    public async Task<string> GetSummarizationPromptAsync(CancellationToken ct)
    {
        var loaded = await _promptManager.GetPromptAsync("system/compact", ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(loaded) ? FallbackCompactPrompt : loaded;
    }

    /// <summary>
    /// 构建压缩系统提示词——主提示词从 <c>prompts/system/compact.prompt</c> 加载（用户/团队可覆盖）。
    /// <paramref name="customInstructions"/> 作为运行时追加段拼接到末尾。
    /// </summary>
    public async Task<string> BuildSystemPromptAsync(string? customInstructions, CancellationToken ct)
    {
        var basePrompt = await GetSummarizationPromptAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(customInstructions))
            return basePrompt;

        return new StringBuilder(basePrompt)
            .Append("\n\nAdditional Instructions:\n")
            .Append(customInstructions)
            .ToString();
    }

    /// <summary>
    /// Builds the chat messages + chat options to send to the summarisation model.
    /// Appends the "Please create a detailed summary" user turn, maps domain messages
    /// to MAF <see cref="ChatMessage"/>s, and inserts the system prompt at index 0.
    /// </summary>
    public CompactChatRequest BuildChatRequest(
        IReadOnlyList<Message> messagesToCompact,
        string systemPrompt,
        string model)
    {
        var compactMessages = messagesToCompact.ToList();
        compactMessages.Add(new UserMessage(
            Id: Guid.NewGuid().ToString("N"),
            Content: "Please create a detailed summary of our conversation above.",
            Timestamp: DateTimeOffset.UtcNow));

        var chatMessages = compactMessages.Select(MapDomainMessageToChatMessage).ToList();
        // 长对话压缩涉及长上下文理解，质量直接影响后续会话连续性，统一使用主模型。
        var options = new ChatOptions
        {
            ModelId = model,
            MaxOutputTokens = 8192,
        };

        // System prompt as first message
        chatMessages.Insert(0, new ChatMessage(ChatRole.System, systemPrompt));

        return new CompactChatRequest(chatMessages, options);
    }

    /// <summary>
    /// Strip the &lt;analysis&gt; scratchpad and unwrap the &lt;summary&gt; tags,
    /// matching the TypeScript formatCompactSummary() behaviour.
    /// </summary>
    public static string FormatSummary(string raw)
    {
        var formatted = AnalysisBlockRegex().Replace(raw, string.Empty);

        var match = SummaryBlockRegex().Match(formatted);
        if (match.Success)
        {
            var inner = match.Groups[1].Value.Trim();
            formatted = SummaryBlockRegex().Replace(formatted, $"Summary:\n{inner}");
        }

        return formatted.Trim();
    }

    /// <summary>
    /// Maps a domain <see cref="Message"/> to a MAF <see cref="ChatMessage"/>.
    /// Unknown message types fall back to a user-role placeholder so the chat history
    /// remains structurally valid even if the model never sees the original content.
    /// </summary>
    public static ChatMessage MapDomainMessageToChatMessage(Message msg) => msg switch
    {
        UserMessage um => new ChatMessage(ChatRole.User, um.Content),
        AssistantMessage am => new ChatMessage(ChatRole.Assistant, string.Join("\n", am.Content.OfType<TextBlock>().Select(b => b.Text))),
        SystemMessage sm => new ChatMessage(ChatRole.System, sm.Content),
        ToolResultMessage trm => new ChatMessage(ChatRole.User, trm.Content),
        _ => new ChatMessage(ChatRole.User, $"[Unsupported message type: {msg.GetType().Name}]")
    };
}

/// <summary>Result of <see cref="CompactPromptBuilder.BuildChatRequest"/>: the messages and options to send to the model.</summary>
public sealed record CompactChatRequest(IReadOnlyList<ChatMessage> ChatMessages, ChatOptions Options);
