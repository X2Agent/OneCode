using System.Text;
using Microsoft.Extensions.AI;
using OneCode.Core.Prompt;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Services.Compact;

/// <summary>
/// Builds the system prompt, chat messages, and chat options used to invoke the
/// summarisation model during a compact run.
/// </summary>
public sealed class CompactPromptBuilder(IPromptManager promptManager)
{
    private readonly IPromptManager _promptManager = promptManager;

    /// <summary>
    /// 加载压缩摘要 prompt（<c>system/compact</c>）。显式 <c>/compact</c> 与 MAF in-pipeline 压缩共用。
    /// prompt 经 csproj Content + EmbeddedResource 双重打包，生产环境必然存在；
    /// 缺失属于打包损坏，fail-fast 抛出（与其他系统 prompt 策略一致），由调用方决定呈现方式。
    /// </summary>
    public async Task<string> GetSummarizationPromptAsync(CancellationToken ct)
    {
        var loaded = await _promptManager.GetPromptAsync("system/compact", ct).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(loaded)
            ? loaded
            : throw new InvalidOperationException(
                "Prompt 'system/compact' is not available in any IPromptManager store.");
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
            MaxOutputTokens = SummarizationDefaults.MaxOutputTokens,
        };

        // System prompt as first message
        chatMessages.Insert(0, new ChatMessage(ChatRole.System, systemPrompt));

        return new CompactChatRequest(chatMessages, options);
    }

    /// <summary>
    /// Normalises the summariser's raw output into the stored summary text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both compact paths (explicit <c>/compact</c> and the in-pipeline MAF summarisation strategy)
    /// share one contract: the model returns the work summary directly, with no <c>&lt;analysis&gt;</c>
    /// scratchpad and no <c>&lt;summary&gt;</c> wrapper. The old contract asked for both blocks and then
    /// stripped them here, which only worked for the explicit path — the automatic path stored the
    /// raw tagged text verbatim, so the same conversation produced two different summary formats
    /// depending on which path ran.
    /// </para>
    /// <para>
    /// Trimming is still applied: leading/trailing whitespace carries no meaning and would otherwise
    /// be inserted verbatim into the transcript.
    /// </para>
    /// </remarks>
    public static string FormatSummary(string raw) => raw.Trim();

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
