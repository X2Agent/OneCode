using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using OneCode.Infrastructure;
using OneCode.Infrastructure.Config;
using CoreConstants = OneCode.Core.Constants;

namespace OneCode.App.Session;

public sealed class SessionStore : ISessionStore
{
    private readonly string _sessionsDir;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly ILogger<SessionStore> _logger;

    public SessionStore(string? basePath = null, ILogger<SessionStore>? logger = null)
    {
        _logger = logger ?? NullLogger<SessionStore>.Instance;
        var homeDir = basePath ?? PathsHelper.UserHome;
        _sessionsDir = Path.Combine(homeDir, Constants.App.ConfigDirName, "sessions");
        Directory.CreateDirectory(_sessionsDir);
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower), new ContentBlockConverter() },
        };
    }

    public async Task<Conversation?> LoadAsync(SessionId conversationId, CancellationToken ct = default)
    {
        var file = GetSessionFile(conversationId);
        if (!File.Exists(file))
            return null;

        var lines = await ReadAllLinesAsync(file, ct);
        if (lines.Count == 0)
            return null;

        var header = TryParseHeader(lines[0]);
        var conversation = header != null
            ? header.ToConversation()
            : new Conversation { Id = conversationId };

        var startIndex = header != null ? 1 : 0;
        for (var i = startIndex; i < lines.Count; i++)
        {
            var msg = DeserializeDomainMessage(lines[i]);
            if (msg != null)
                conversation.Messages.Add(msg);
        }

        if (header == null)
        {
            var stat = new FileInfo(file);
            var firstUserMsg = conversation.Messages.OfType<UserMessage>().FirstOrDefault();
            conversation.Name = Truncate(firstUserMsg?.Content ?? "Untitled", 60);
            conversation.LastActivityAt = stat.LastWriteTime;
            conversation.CreatedAt = stat.CreationTime;
        }

        return conversation;
    }

    public async Task SaveAsync(Conversation conversation, CancellationToken ct = default)
    {
        await _saveLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = GetSessionFile(conversation.Id);
            var tempFile = file + ".tmp";

            {
                await using var stream = File.Create(tempFile);
                await using var writer = new StreamWriter(stream);

                var header = new SessionHeaderRecord(
                    conversation.Id,
                    conversation.Name,
                    conversation.WorkingDirectory,
                    conversation.Model,
                    conversation.Status,
                    conversation.TotalUsage,
                    conversation.CreatedAt,
                    conversation.LastActivityAt,
                    conversation.Branch,
                    conversation.Messages.Count,
                    conversation.Metadata.Count > 0 ? conversation.Metadata : null);

                var headerJson = JsonSerializer.Serialize(header, _jsonOptions);
                await writer.WriteLineAsync(headerJson);

                foreach (var msg in conversation.Messages)
                {
                    var json = JsonSerializer.Serialize(msg, msg.GetType(), _jsonOptions);
                    await writer.WriteLineAsync(json);
                }

                await writer.FlushAsync(ct);
            }

            await RetryMoveAsync(tempFile, file, ct: ct);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public async Task<IReadOnlyList<Conversation>> ListAsync(CancellationToken ct = default)
    {
        var sessionFiles = Directory.GetFiles(_sessionsDir, "*.jsonl");
        List<Conversation> conversations = [];

        foreach (var file in sessionFiles)
        {
            var sessionId = Path.GetFileNameWithoutExtension(file);
            if (!SessionId.TryParse(sessionId).HasValue)
                continue;

            try
            {
                var firstLine = await ReadFirstLineAsync(file, ct);
                if (string.IsNullOrEmpty(firstLine))
                    continue;

                var header = TryParseHeader(firstLine);
                if (header != null)
                {
                    conversations.Add(header.ToConversation());
                }
                else
                {
                    var stat = new FileInfo(file);
                    conversations.Add(new Conversation
                    {
                        Id = SessionId.TryParse(sessionId) ?? SessionId.NewId(),
                        Name = Truncate(ExtractField(firstLine, "first_prompt") ?? "Untitled", 60),
                        LastActivityAt = stat.LastWriteTime,
                        CreatedAt = stat.CreationTime,
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read session file {File}; skipping", file);
            }
        }

        return conversations;
    }

    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(
        int? limit = null,
        int? offset = null,
        CancellationToken ct = default)
    {
        var sessionFiles = Directory.GetFiles(_sessionsDir, "*.jsonl");
        List<SessionCandidate> candidates = [];

        foreach (var file in sessionFiles)
        {
            var sessionId = Path.GetFileNameWithoutExtension(file);
            if (!SessionId.TryParse(sessionId).HasValue)
                continue;

            var stat = new FileInfo(file);
            candidates.Add(new SessionCandidate(sessionId, file, stat.LastWriteTime, stat.Length));
        }

        candidates.Sort((a, b) => b.LastModified.CompareTo(a.LastModified));

        List<SessionSummary> sessions = [];
        var skip = offset ?? 0;
        var limitVal = limit ?? int.MaxValue;
        var read = 0;
        var skipped = 0;

        foreach (var candidate in candidates)
        {
            if (skipped < skip) { skipped++; continue; }
            if (read >= limitVal) break;

            var summary = await ReadSessionSummaryAsync(candidate.FilePath, ct);
            if (summary != null)
            {
                sessions.Add(summary);
                read++;
            }
        }

        return sessions;
    }

    public async Task<SessionResume?> LoadForResumeAsync(SessionId sessionId, CancellationToken ct = default)
    {
        var file = GetSessionFile(sessionId);
        if (!File.Exists(file))
            return null;

        var lines = await ReadAllLinesAsync(file, ct);
        if (lines.Count == 0)
            return null;

        var startIndex = TryParseHeader(lines[0]) != null ? 1 : 0;
        List<Message> messages = [];
        for (var i = startIndex; i < lines.Count; i++)
        {
            var msg = DeserializeDomainMessage(lines[i]);
            if (msg != null)
                messages.Add(msg);
        }

        if (messages.Count == 0)
            return null;

        // Filter out assistant messages with unresolved tool uses (tool_use blocks
        // without corresponding tool_result messages).
        var resolvedToolUseIds = messages
            .OfType<ToolResultMessage>()
            .Select(m => m.ToolUseId)
            .ToHashSet(StringComparer.Ordinal);
        var filtered = messages
            .Where(m => m is not AssistantMessage am || !HasUnresolvedToolUses(am, resolvedToolUseIds))
            .ToList();

        var lastMessage = filtered.FindLast(m => m.Role is not MessageRole.System and not MessageRole.Tool);
        var interruptionState = lastMessage?.Role switch
        {
            MessageRole.User => InterruptionState.InterruptedPrompt,
            MessageRole.Attachment => InterruptionState.InterruptedTurn,
            _ => InterruptionState.None,
        };

        var title = filtered.OfType<UserMessage>().FirstOrDefault()?.Content.TrimStart();
        title = string.IsNullOrEmpty(title) ? "(untitled)" : Truncate(title, 60);

        return new SessionResume(
            SessionId: sessionId,
            Messages: filtered,
            InterruptionState: interruptionState,
            LastModified: new FileInfo(file).LastWriteTime,
            Title: title,
            MessageCount: filtered.Count
        );
    }

    private static bool HasUnresolvedToolUses(AssistantMessage msg, HashSet<string> resolvedToolUseIds)
    {
        foreach (var block in msg.Content.OfType<ToolUseBlock>())
        {
            if (!resolvedToolUseIds.Contains(block.Id))
                return true;
        }
        return false;
    }

    public void Delete(SessionId sessionId)
    {
        var file = GetSessionFile(sessionId);
        if (File.Exists(file))
            File.Delete(file);
    }

    private string GetSessionFile(SessionId sessionId) =>
        Path.Combine(_sessionsDir, $"{sessionId.Value}{CoreConstants.Session.SessionFileExtension}");

    private static async Task RetryMoveAsync(string source, string dest, int maxRetries = 5, CancellationToken ct = default)
    {
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                if (File.Exists(dest))
                    File.Delete(dest);
                File.Move(source, dest);
                return;
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                await Task.Delay(50 * (i + 1), ct).ConfigureAwait(false);
            }
        }
    }

    private Message? DeserializeDomainMessage(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("role", out var roleProp))
                return null;

            var role = roleProp.GetString();
            return role switch
            {
                CoreConstants.MessageTypes.User => JsonSerializer.Deserialize<UserMessage>(line, _jsonOptions),
                CoreConstants.MessageTypes.Assistant => JsonSerializer.Deserialize<AssistantMessage>(line, _jsonOptions),
                "system" => JsonSerializer.Deserialize<SystemMessage>(line, _jsonOptions),
                "tool" => JsonSerializer.Deserialize<ToolResultMessage>(line, _jsonOptions),
                "attachment" => JsonSerializer.Deserialize<AttachmentMessage>(line, _jsonOptions),
                "tombstone" => JsonSerializer.Deserialize<TombstoneMessage>(line, _jsonOptions),
                _ => null,
            };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize domain message line");
            return null;
        }
    }

    private SessionHeaderRecord? TryParseHeader(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("type", out var typeProp) &&
                typeProp.GetString() == "session_header")
            {
                return JsonSerializer.Deserialize<SessionHeaderRecord>(line, _jsonOptions);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse session header line");
        }
        return null;
    }

    private async Task<SessionSummary?> ReadSessionSummaryAsync(string file, CancellationToken ct)
    {
        try
        {
            var stat = new FileInfo(file);
            var sessionId = SessionId.TryParse(Path.GetFileNameWithoutExtension(file)) ?? SessionId.NewId();

            var firstLine = await ReadFirstLineAsync(file, ct);
            if (string.IsNullOrEmpty(firstLine))
                return null;

            if (firstLine.Contains("\"is_sidechain\":true", StringComparison.Ordinal))
                return null;

            var lastLines = await ReadLastLinesAsync(file, 10, ct);
            var title = ExtractField(lastLines, "custom_title")
                ?? ExtractField(lastLines, "ai_title")
                ?? ExtractField(firstLine, "first_prompt");

            if (string.IsNullOrEmpty(title))
                return null;

            return new SessionSummary(
                Id: sessionId,
                Title: Truncate(title, 80),
                MessageCount: 0,
                LastActivityAt: stat.LastWriteTime,
                FirstMessage: ExtractField(firstLine, "first_prompt"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read session summary from {File}", file);
            return null;
        }
    }

    private string? ExtractField(string line, string fieldName)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty(fieldName, out var element))
            {
                return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read field {Field} from session line", fieldName);
            return null;
        }
    }

    private static async Task<string?> ReadFirstLineAsync(string file, CancellationToken ct)
    {
        using var stream = new FileStream(
            file,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return await reader.ReadLineAsync(ct);
    }

    private static async Task<string> ReadLastLinesAsync(string file, int count, CancellationToken ct)
    {
        using var stream = new FileStream(
            file,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        List<string> lines = [];
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            lines.Add(line);
            if (lines.Count > count)
                lines.RemoveAt(0);
        }
        return string.Join("\n", lines);
    }

    private static async Task<List<string>> ReadAllLinesAsync(string file, CancellationToken ct)
    {
        List<string> lines = [];
        await using var stream = new FileStream(
            file,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
            lines.Add(line);
        return lines;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value.Substring(0, maxLength - 3) + "...";

}

public sealed record SessionResume(
    SessionId SessionId,
    IReadOnlyList<Message> Messages,
    InterruptionState InterruptionState,
    DateTimeOffset LastModified,
    string? Title,
    int MessageCount);

public enum InterruptionState
{
    None,
    InterruptedPrompt,
    InterruptedTurn
}

internal sealed record SessionCandidate(
    string SessionId,
    string FilePath,
    DateTimeOffset LastModified,
    long Size);

internal sealed record SessionHeaderRecord(
    SessionId Id,
    string Name,
    string WorkingDirectory,
    string Model,
    ConversationStatus Status,
    TokenUsage TotalUsage,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    string? Branch,
    int MessageCount,
    Dictionary<string, object>? Metadata)
{
    public string Type { get; init; } = "session_header";

    public Conversation ToConversation()
    {
        var conv = new Conversation
        {
            Id = Id,
            Name = Name,
            WorkingDirectory = WorkingDirectory,
            Model = Model,
            Status = Status,
            TotalUsage = TotalUsage,
            CreatedAt = CreatedAt,
            LastActivityAt = LastActivityAt,
            Branch = Branch,
        };
        if (Metadata != null)
        {
            foreach (var (key, value) in Metadata)
                conv.Metadata[key] = value;
        }
        return conv;
    }
}

internal sealed class ContentBlockConverter : JsonConverter<ContentBlock>
{
    private static readonly JsonSerializerOptions InnerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public override ContentBlock? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

        return type switch
        {
            "text" => JsonSerializer.Deserialize<TextBlock>(root.GetRawText(), InnerOptions),
            "tool_use" => JsonSerializer.Deserialize<ToolUseBlock>(root.GetRawText(), InnerOptions),
            "thinking" => JsonSerializer.Deserialize<ThinkingBlock>(root.GetRawText(), InnerOptions),
            "redacted_thinking" => JsonSerializer.Deserialize<RedactedThinkingBlock>(root.GetRawText(), InnerOptions),
            _ => InferFromProperties(root),
        };
    }

    public override void Write(Utf8JsonWriter writer, ContentBlock value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value switch
        {
            TextBlock => "text",
            ToolUseBlock => "tool_use",
            ThinkingBlock => "thinking",
            RedactedThinkingBlock => "redacted_thinking",
            _ => "unknown",
        });

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value, value.GetType(), InnerOptions));
        foreach (var prop in doc.RootElement.EnumerateObject())
            prop.WriteTo(writer);

        writer.WriteEndObject();
    }

    private static ContentBlock? InferFromProperties(JsonElement root)
    {
        if (root.TryGetProperty("text", out _) && !root.TryGetProperty("name", out _))
            return JsonSerializer.Deserialize<TextBlock>(root.GetRawText(), InnerOptions);
        if (root.TryGetProperty("name", out _))
            return JsonSerializer.Deserialize<ToolUseBlock>(root.GetRawText(), InnerOptions);
        if (root.TryGetProperty("thinking", out _))
            return JsonSerializer.Deserialize<ThinkingBlock>(root.GetRawText(), InnerOptions);
        if (root.TryGetProperty("data", out _))
            return JsonSerializer.Deserialize<RedactedThinkingBlock>(root.GetRawText(), InnerOptions);
        return null;
    }
}
