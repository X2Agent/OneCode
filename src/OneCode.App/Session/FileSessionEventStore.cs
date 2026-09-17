using System.Text.Json.Serialization;
using OneCode.Core.Session;
using InfraConstants = OneCode.Infrastructure.Config.Constants;

namespace OneCode.App.Session;

/// <summary>按 session 文件追加事件的 JSONL 存储。</summary>
public sealed class FileSessionEventStore : ISessionEventStore
{
    private readonly string _eventsDirectory;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ConcurrentDictionary<SessionId, SemaphoreSlim> _sessionLocks = new();

    public FileSessionEventStore(string basePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        _eventsDirectory = Path.Combine(basePath, InfraConstants.App.ConfigDirName, InfraConstants.Subdirs.Events);
        Directory.CreateDirectory(_eventsDirectory);
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower),
                new ContentBlockConverter(),
            },
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> AppendAsync(
        IReadOnlyList<SessionEvent> events,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
            return [];

        var sessionId = events[0].SessionId;
        if (events.Any(sessionEvent => sessionEvent.SessionId != sessionId))
            throw new ArgumentException("All events in a batch must belong to the same session.", nameof(events));

        var sessionLock = _sessionLocks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await ReadCoreAsync(sessionId, ct).ConfigureAwait(false);
            var firstSequence = existing.Count == 0
                ? 1L
                : existing.Max(sessionEvent => sessionEvent.Sequence) + 1L;
            var sequences = new long[events.Count];
            var file = GetEventFile(sessionId);
            var temporaryFile = file + ".tmp-" + Guid.NewGuid().ToString("N");

            try
            {
                await using (var stream = new FileStream(
                    temporaryFile,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 16 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    if (File.Exists(file))
                    {
                        await using var existingStream = new FileStream(
                            file,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            bufferSize: 16 * 1024,
                            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await existingStream.CopyToAsync(stream, ct).ConfigureAwait(false);
                    }

                    for (var index = 0; index < events.Count; index++)
                    {
                        var sequence = firstSequence + index;
                        await stream.WriteAsync(
                            Serialize(events[index].WithSequence(sequence)), ct).ConfigureAwait(false);
                        sequences[index] = sequence;
                    }

                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }

                File.Move(temporaryFile, file, overwrite: true);
                return sequences;
            }
            finally
            {
                if (File.Exists(temporaryFile))
                    File.Delete(temporaryFile);
            }
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionEvent>> ReadAsync(
        SessionId sessionId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadCoreAsync(sessionId, ct).ConfigureAwait(false);
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SessionId>> ListSessionsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var ids = Directory.EnumerateFiles(_eventsDirectory, "*.jsonl")
            .Select(file => SessionId.TryParse(Path.GetFileNameWithoutExtension(file)))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToArray();
        return Task.FromResult<IReadOnlyList<SessionId>>(ids);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(SessionId sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = GetEventFile(sessionId);
            if (File.Exists(file))
                File.Delete(file);
        }
        finally
        {
            sessionLock.Release();
        }
    }

    private async Task<IReadOnlyList<SessionEvent>> ReadCoreAsync(
        SessionId sessionId,
        CancellationToken ct)
    {
        var file = GetEventFile(sessionId);
        if (!File.Exists(file))
            return [];

        var lines = new List<string>();
        await using var stream = new FileStream(
            file,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 16 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            lines.Add(line);
        }

        var result = new List<SessionEvent>(lines.Count);
        for (var index = 0; index < lines.Count; index++)
        {
            try
            {
                result.Add(Deserialize(lines[index]));
            }
            catch (JsonException) when (index == lines.Count - 1)
            {
                break;
            }
        }

        return result;
    }

    private byte[] Serialize(SessionEvent sessionEvent)
    {
        var payload = sessionEvent switch
        {
            SessionStartedEvent item => JsonSerializer.SerializeToElement(
                new SessionStartedPayload(
                    item.Name,
                    item.WorkingDirectory,
                    item.Model,
                    item.Status,
                    item.TotalUsage,
                    item.CreatedAt,
                    item.LastActivityAt,
                    item.Branch,
                    item.Metadata), _jsonOptions),
            SessionSnapshotEvent item => JsonSerializer.SerializeToElement(
                new SessionStartedPayload(
                    item.Name,
                    item.WorkingDirectory,
                    item.Model,
                    item.Status,
                    item.TotalUsage,
                    item.CreatedAt,
                    item.LastActivityAt,
                    item.Branch,
                    item.Metadata), _jsonOptions),
            UserMessageEvent item => JsonSerializer.SerializeToElement(item.Message, _jsonOptions),
            SystemMessageEvent item => JsonSerializer.SerializeToElement(item.Message, _jsonOptions),
            AssistantMessageEvent item => JsonSerializer.SerializeToElement(item.Message, _jsonOptions),
            ToolResultEvent item => JsonSerializer.SerializeToElement(item.Message, _jsonOptions),
            MessagesReplacedEvent item => JsonSerializer.SerializeToElement(
                new MessagesReplacedPayload(
                    item.Messages.Select(SerializeMessage).ToArray(),
                    item.Reason), _jsonOptions),
            AttachmentMessageEvent item => JsonSerializer.SerializeToElement(item.Message, _jsonOptions),
            TombstoneMessageEvent item => JsonSerializer.SerializeToElement(item.Message, _jsonOptions),
            SessionMarkerEvent item => JsonSerializer.SerializeToElement(item.Data, _jsonOptions),
            _ => throw new InvalidOperationException($"Unsupported session event: {sessionEvent.GetType().Name}"),
        };

        var envelope = new PersistedSessionEvent(
            SchemaVersion: 1,
            sessionEvent.SessionId,
            sessionEvent.Sequence,
            sessionEvent.OccurredAt,
            sessionEvent.Type,
            sessionEvent.Ignorable,
            payload);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, _jsonOptions)
            .Append((byte)'\n')
            .ToArray();
    }

    private SessionEvent Deserialize(string line)
    {
        var envelope = JsonSerializer.Deserialize<PersistedSessionEvent>(line, _jsonOptions)
            ?? throw new JsonException("Session event envelope is null.");

        return envelope.Type switch
        {
            SessionEventType.SessionStarted => CreateSessionStartedEvent(envelope),
            SessionEventType.SessionSnapshot => CreateSessionSnapshotEvent(envelope),
            SessionEventType.UserMessage => new UserMessageEvent(
                envelope.SessionId,
                envelope.Sequence,
                envelope.OccurredAt,
                envelope.Payload.Deserialize<UserMessage>(_jsonOptions)
                    ?? throw new JsonException("User message payload is null.")),
            SessionEventType.SystemMessage => new SystemMessageEvent(
                envelope.SessionId,
                envelope.Sequence,
                envelope.OccurredAt,
                envelope.Payload.Deserialize<SystemMessage>(_jsonOptions)
                    ?? throw new JsonException("System message payload is null.")),
            SessionEventType.AssistantMessage => new AssistantMessageEvent(
                envelope.SessionId,
                envelope.Sequence,
                envelope.OccurredAt,
                envelope.Payload.Deserialize<AssistantMessage>(_jsonOptions)
                    ?? throw new JsonException("Assistant message payload is null.")),
            SessionEventType.ToolResult => new ToolResultEvent(
                envelope.SessionId,
                envelope.Sequence,
                envelope.OccurredAt,
                envelope.Payload.Deserialize<ToolResultMessage>(_jsonOptions)
                    ?? throw new JsonException("Tool result payload is null.")),
            SessionEventType.MessagesReplaced => CreateMessagesReplacedEvent(envelope),
            SessionEventType.AttachmentMessage => new AttachmentMessageEvent(
                envelope.SessionId,
                envelope.Sequence,
                envelope.OccurredAt,
                envelope.Payload.Deserialize<AttachmentMessage>(_jsonOptions)
                    ?? throw new JsonException("Attachment payload is null.")),
            SessionEventType.TombstoneMessage => new TombstoneMessageEvent(
                envelope.SessionId,
                envelope.Sequence,
                envelope.OccurredAt,
                envelope.Payload.Deserialize<TombstoneMessage>(_jsonOptions)
                    ?? throw new JsonException("Tombstone payload is null.")),
            _ => new SessionMarkerEvent(
                envelope.SessionId,
                envelope.Sequence,
                envelope.OccurredAt,
                envelope.Type,
                envelope.Payload.ValueKind is JsonValueKind.String
                    ? envelope.Payload.GetString()
                    : null,
                envelope.Ignorable),
        };
    }

    private SessionStartedEvent CreateSessionStartedEvent(PersistedSessionEvent envelope)
    {
        var payload = envelope.Payload.Deserialize<SessionStartedPayload>(_jsonOptions)
            ?? throw new JsonException("Session metadata payload is null.");
        return new SessionStartedEvent(
            envelope.SessionId,
            envelope.Sequence,
            envelope.OccurredAt,
            payload.Name,
            payload.WorkingDirectory,
            payload.Model,
            payload.Status,
            payload.TotalUsage,
            payload.CreatedAt,
            payload.LastActivityAt,
            payload.Branch,
            payload.Metadata);
    }

    private SessionSnapshotEvent CreateSessionSnapshotEvent(PersistedSessionEvent envelope)
    {
        var payload = envelope.Payload.Deserialize<SessionStartedPayload>(_jsonOptions)
            ?? throw new JsonException("Session snapshot payload is null.");
        return new SessionSnapshotEvent(
            envelope.SessionId,
            envelope.Sequence,
            envelope.OccurredAt,
            payload.Name,
            payload.WorkingDirectory,
            payload.Model,
            payload.Status,
            payload.TotalUsage,
            payload.CreatedAt,
            payload.LastActivityAt,
            payload.Branch,
            payload.Metadata);
    }

    private MessagesReplacedEvent CreateMessagesReplacedEvent(PersistedSessionEvent envelope)
    {
        var payload = envelope.Payload.Deserialize<MessagesReplacedPayload>(_jsonOptions)
            ?? throw new JsonException("Messages replacement payload is null.");
        return new MessagesReplacedEvent(
            envelope.SessionId,
            envelope.Sequence,
            envelope.OccurredAt,
            payload.Messages.Select(DeserializeMessage).ToArray(),
            payload.Reason);
    }

    private MessagePayload SerializeMessage(Message message) => message switch
    {
        UserMessage item => new MessagePayload("user", JsonSerializer.SerializeToElement(item, _jsonOptions)),
        AssistantMessage item => new MessagePayload("assistant", JsonSerializer.SerializeToElement(item, _jsonOptions)),
        SystemMessage item => new MessagePayload("system", JsonSerializer.SerializeToElement(item, _jsonOptions)),
        ToolResultMessage item => new MessagePayload("tool", JsonSerializer.SerializeToElement(item, _jsonOptions)),
        AttachmentMessage item => new MessagePayload("attachment", JsonSerializer.SerializeToElement(item, _jsonOptions)),
        TombstoneMessage item => new MessagePayload("tombstone", JsonSerializer.SerializeToElement(item, _jsonOptions)),
        _ => throw new InvalidOperationException($"Unsupported conversation message: {message.GetType().Name}"),
    };

    private Message DeserializeMessage(MessagePayload payload) => payload.Type switch
    {
        "user" => payload.Value.Deserialize<UserMessage>(_jsonOptions)
            ?? throw new JsonException("User message payload is null."),
        "assistant" => payload.Value.Deserialize<AssistantMessage>(_jsonOptions)
            ?? throw new JsonException("Assistant message payload is null."),
        "system" => payload.Value.Deserialize<SystemMessage>(_jsonOptions)
            ?? throw new JsonException("System message payload is null."),
        "tool" => payload.Value.Deserialize<ToolResultMessage>(_jsonOptions)
            ?? throw new JsonException("Tool result payload is null."),
        "attachment" => payload.Value.Deserialize<AttachmentMessage>(_jsonOptions)
            ?? throw new JsonException("Attachment message payload is null."),
        "tombstone" => payload.Value.Deserialize<TombstoneMessage>(_jsonOptions)
            ?? throw new JsonException("Tombstone message payload is null."),
        _ => throw new JsonException($"Unknown message discriminator: {payload.Type}"),
    };

    private string GetEventFile(SessionId sessionId) =>
        Path.Combine(_eventsDirectory, $"{sessionId.Value}.jsonl");

    private sealed record PersistedSessionEvent(
        int SchemaVersion,
        SessionId SessionId,
        long Sequence,
        DateTimeOffset OccurredAt,
        SessionEventType Type,
        bool Ignorable,
        JsonElement Payload);

    private sealed record SessionStartedPayload(
        string Name,
        string WorkingDirectory,
        string Model,
        ConversationStatus Status,
        TokenUsage TotalUsage,
        DateTimeOffset CreatedAt,
        DateTimeOffset LastActivityAt,
        string? Branch,
        IReadOnlyDictionary<string, object>? Metadata);

    private sealed record MessagesReplacedPayload(
        IReadOnlyList<MessagePayload> Messages,
        string Reason);

    private sealed record MessagePayload(string Type, JsonElement Value);
}