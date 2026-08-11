using System.Text.RegularExpressions;

namespace OneCode.App.Services.Memory;

public sealed partial class SessionMemoryService : ISessionMemoryService
{
    private const string SessionMemoriesKey = "sessionMemories";

    [GeneratedRegex(@"(?:\r?\n)+|(?<=[\.\!\?。！？；;])\s+")]
    private static partial Regex SentenceSplitRegex();

    [GeneratedRegex(@"[^a-zA-Z0-9\-_]")]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceCollapseRegex();

    private static readonly string[] PreferenceSignals =
    {
        "prefer", "always", "never", "remember", "important", "deadline", "must", "should",
        "use ", "don't", "do not", "avoid", "priority", "偏好", "记住", "不要", "必须", "优先", "截止"
    };

    private readonly ILogger<SessionMemoryService> _logger;

    public SessionMemoryService(ILogger<SessionMemoryService> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<SessionMemoryEntry>> MergeExtractedMemoriesAsync(
        Conversation conversation,
        IEnumerable<string> candidateMemories,
        string source = "auto",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var memories = ReadMemories(conversation.Metadata, _logger).ToList();

        foreach (var candidate in candidateMemories)
        {
            var normalized = NormalizeMemory(candidate);
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            var existing = memories.FirstOrDefault(m => string.Equals(m.Content, normalized, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                Replace(memories, existing.Id, existing with { UpdatedAt = DateTimeOffset.UtcNow, Source = source });
                continue;
            }

            memories.Add(new SessionMemoryEntry(
                Guid.NewGuid().ToString("N"),
                normalized,
                source,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
        }

        WriteMemories(conversation.Metadata, memories);
        return memories;
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionMemoryEntry> GetMemories(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return ReadMemories(conversation.Metadata, _logger);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ExtractKeyFacts(Conversation conversation, int maxFacts = 5)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        List<string> candidates = [];
        foreach (var message in conversation.Messages.TakeLast(24))
        {
            foreach (var text in ExtractMessageText(message))
            {
                foreach (var sentence in SentenceSplitRegex().Split(text))
                {
                    var cleaned = NormalizeMemory(sentence);
                    if (ShouldKeep(cleaned, message.Role))
                        candidates.Add(cleaned);
                }
            }
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxFacts))
            .ToList();
    }

    private static IEnumerable<string> ExtractMessageText(Message message)
    {
        return message switch
        {
            UserMessage user => [user.Content],
            AssistantMessage assistant => assistant.Content.OfType<TextBlock>().Select(block => block.Text),
            SystemMessage system => [system.Content],
            ToolResultMessage tool => [tool.Content],
            _ => Array.Empty<string>(),
        };
    }

    private static bool ShouldKeep(string text, MessageRole role)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 12 || text.Length > 220)
            return false;
        if (text.StartsWith("/", StringComparison.Ordinal))
            return false;
        if (PreferenceSignals.Any(signal => text.Contains(signal, StringComparison.OrdinalIgnoreCase)))
            return true;
        return role == MessageRole.User && text.Length >= 24;
    }

    private static string NormalizeMemory(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var normalized = text.Trim();
        normalized = normalized.TrimStart('-', '*', '•', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ')', ' ');
        normalized = WhitespaceCollapseRegex().Replace(normalized, " ");
        return normalized.Trim();
    }

    private static IReadOnlyList<SessionMemoryEntry> ReadMemories(
        Dictionary<string, object> metadata, ILogger? logger = null)
    {
        if (!metadata.TryGetValue(SessionMemoriesKey, out var raw) || raw == null)
            return Array.Empty<SessionMemoryEntry>();
        try
        {
            var json = raw switch
            {
                string s => s,
                JsonElement el => el.ValueKind == JsonValueKind.String ? el.GetString() ?? "[]" : el.GetRawText(),
                _ => JsonSerializer.Serialize(raw),
            };
            return JsonSerializer.Deserialize<List<SessionMemoryEntry>>(json)
                ?? (List<SessionMemoryEntry>)[];
        }
        catch (Exception ex)
        {
            // 会话记忆反序列化失败 → 返回空集合并留痕（此前静默吞掉，损坏数据无任何信号）。
            logger?.LogWarning(ex, "Failed to deserialize session memories — returning empty list");
            return Array.Empty<SessionMemoryEntry>();
        }
    }

    private static void WriteMemories(Dictionary<string, object> metadata, IReadOnlyList<SessionMemoryEntry> memories)
    {
        metadata[SessionMemoriesKey] = JsonSerializer.Serialize(memories);
    }

    private static void Replace(List<SessionMemoryEntry> memories, string id, SessionMemoryEntry updated)
    {
        var index = memories.FindIndex(m => m.Id == id);
        if (index >= 0)
            memories[index] = updated;
    }
}

public sealed record SessionMemoryEntry(
    string Id,
    string Content,
    string Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt = null,
    double Importance = 0.5);
