using OneCode.Core.Domain;
using OneCode.Core.Session;
using InfraConstants = OneCode.Infrastructure.Config.Constants;
using CoreConstants = OneCode.Core.Constants;

namespace OneCode.Infrastructure.Persistence;

/// <summary>按 session 文件追加事件的 JSONL 存储。</summary>
public sealed class FileSessionEventStore : ISessionEventStore
{
    private readonly string _eventsDirectory;
    private readonly string _lockDirectory;
    private readonly ILogger<FileSessionEventStore>? _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ConcurrentDictionary<SessionId, SemaphoreSlim> _sessionLocks = new();

    /// <summary>
    /// 尾读窗口：从文件尾部读取该字节数以解析最后一条事件的序列号（O(Δ) 追加的前置）。
    /// 快照/替换类巨行超出窗口时回退整文件一次读入（低频维护路径）。
    /// </summary>
    private const int TailReadWindowBytes = 64 * 1024;

    public FileSessionEventStore(string basePath, ILogger<FileSessionEventStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        _logger = logger;
        _eventsDirectory = Path.Combine(basePath, InfraConstants.App.ConfigDirName, InfraConstants.Subdirs.Events);
        Directory.CreateDirectory(_eventsDirectory);
        _lockDirectory = Path.Combine(_eventsDirectory, "locks");
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
    public async Task<SessionEventLogStamp?> AppendAsync(
        IReadOnlyList<SessionEvent> events,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
            return null;

        var sessionId = events[0].SessionId;
        if (events.Any(sessionEvent => sessionEvent.SessionId != sessionId))
            throw new ArgumentException("All events in a batch must belong to the same session.", nameof(events));

        var sessionLock = _sessionLocks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 跨进程半边：追加在跨进程锁内做，序列号从文件尾部最后一条完整事件推导——
            // 另一进程写入过后尾部自然反映其序列，无需读改写整文件。
            await using var crossProcessLock =
                await DurableFile.AcquireLockAsync(GetLockFile(sessionId), ct: ct).ConfigureAwait(false);

            SessionDeletionGuard.ThrowIfDeleted(_eventsDirectory, sessionId);
            var file = GetEventFile(sessionId);
            var lastSequence = await RecoverTailAsync(file, ct).ConfigureAwait(false);

            // 真追加 O(Δ)：此前为「整文件复制 → Move 覆盖」（单次 O(n)、累计 O(n²) 写放大，
            // 10MB 会话每次追加都要复制 10MB）。JSONL 逐行自包含，尾部推导 + Append 即可。
            await using (var stream = new FileStream(
                file,
                FileMode.Append,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                options: FileOptions.Asynchronous))
            {
                for (var index = 0; index < events.Count; index++)
                {
                    var sequence = lastSequence + 1L + index;
                    await stream.WriteAsync(Serialize(events[index].WithSequence(sequence)), ct).ConfigureAwait(false);
                }

                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            var written = new FileInfo(file);
            return new SessionEventLogStamp(
                sessionId,
                written.Length,
                new DateTimeOffset(written.LastWriteTimeUtc, TimeSpan.Zero));
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <summary>
    /// 尾部恢复：推导文件最后一条完整事件的序列号，并处理崩溃残留的半行（O(尾窗) 而非 O(n)）。
    /// 半行处理：能解析为完整事件 → 补收尾换行；不能解析 → 截断丢弃（该事件未完整落盘，
    /// 与旧实现「仅容忍末行损坏」语义一致，但恢复动作前移到追加前而非读取时）。
    /// </summary>
    private async Task<long> RecoverTailAsync(string file, CancellationToken ct)
    {
        if (!File.Exists(file))
            return 0;

        var length = new FileInfo(file).Length;
        if (length == 0)
            return 0;

        await using var stream = new FileStream(
            file, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 16 * 1024, options: FileOptions.Asynchronous);

        var windowSize = (int)Math.Min(length, TailReadWindowBytes);
        var windowStart = length - windowSize;
        var buffer = new byte[windowSize];
        stream.Seek(windowStart, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);

        // 窗口内无行边界且文件大于窗口（巨行半尾超出窗口）：整文件一次读入处理（低频）。
        if (Array.LastIndexOf(buffer, (byte)'\n') < 0 && windowSize < length)
        {
            windowStart = 0;
            windowSize = (int)length;
            buffer = new byte[windowSize];
            stream.Seek(0, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        }

        // 1) 修复无收尾换行的尾部：完整事件补换行；损坏半行截断丢弃。
        if (buffer[^1] != (byte)'\n')
        {
            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n');
            var tailStart = lastNewline < 0 ? windowStart : windowStart + lastNewline + 1;
            var tailBytes = new byte[length - tailStart];
            stream.Seek(tailStart, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(tailBytes, ct).ConfigureAwait(false);

            if (TryParseSequence(tailBytes, out var tailSequence))
            {
                stream.Seek(length, SeekOrigin.Begin);
                stream.WriteByte((byte)'\n');
                stream.Flush();
                return tailSequence;
            }

            _logger?.LogWarning(
                "Session event log {File} has a corrupted partial tail ({Bytes} bytes); truncating before append",
                file, tailBytes.Length);
            stream.SetLength(tailStart);
            stream.Flush();

            if (lastNewline < 0)
                return 0;
            windowSize = lastNewline + 1;
        }

        // 2) 从窗口尾部向前找最后一条可解析的完整行（损坏行跳过，不再一条坏行废掉整个推导）。
        for (var index = windowSize - 1; index >= 0; index--)
        {
            if (buffer[index] != (byte)'\n')
                continue;

            var previousNewline = index > 0 ? Array.LastIndexOf(buffer, (byte)'\n', index - 1) : -1;
            var lineStart = previousNewline + 1;
            if (TryParseSequence(buffer.AsSpan(lineStart..index), out var sequence))
                return sequence;

            _logger?.LogWarning(
                "Session event log {File} has a corrupted line near the tail; skipping it for sequence recovery",
                file);
            index = previousNewline + 1;
        }

        return 0;
    }

    private static bool TryParseSequence(ReadOnlySpan<byte> line, out long sequence)
    {
        sequence = 0;
        if (line.IsEmpty)
            return false;
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(line);
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("sequence", out var value)
                && value.ValueKind == JsonValueKind.Number
                && (sequence = value.GetInt64()) > 0;
        }
        catch (JsonException)
        {
            return false;
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
    public Task<IReadOnlyList<SessionEventLogStamp>> ListLogsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        List<SessionEventLogStamp> stamps = [];
        foreach (var file in new DirectoryInfo(_eventsDirectory)
            .EnumerateFiles($"*.{CoreConstants.Session.EventsFileExtension}"))
        {
            if (SessionId.TryParse(Path.GetFileNameWithoutExtension(file.Name)) is not { } sessionId)
                continue;

            var lastWrite = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            stamps.Add(new SessionEventLogStamp(sessionId, file.Length, lastWrite));
        }

        return Task.FromResult<IReadOnlyList<SessionEventLogStamp>>(stamps);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(SessionId sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SessionDeletionGuard.MarkDeleted(_eventsDirectory, sessionId);
            // 与 AppendAsync 的追加互斥：防止「一边追加、一边删除」竞态。
            await using var crossProcessLock =
                await DurableFile.AcquireLockAsync(GetLockFile(sessionId), ct: ct).ConfigureAwait(false);

            var file = GetEventFile(sessionId);
            if (File.Exists(file))
                File.Delete(file);
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SessionEventLogStamp?> RewriteAsync(
        SessionId sessionId,
        IReadOnlyList<SessionEvent> events,
        SessionEventLogStamp expectedStamp,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var sessionLock = _sessionLocks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 与 AppendAsync 互斥；整体重写是低频维护操作（快照压缩），允许 O(n)。
            await using var crossProcessLock =
                await DurableFile.AcquireLockAsync(GetLockFile(sessionId), ct: ct).ConfigureAwait(false);

            SessionDeletionGuard.ThrowIfDeleted(_eventsDirectory, sessionId);
            var file = GetEventFile(sessionId);
            var current = new FileInfo(file);
            if (!current.Exists
                || current.Length != expectedStamp.Length
                || new DateTimeOffset(current.LastWriteTimeUtc, TimeSpan.Zero) != expectedStamp.LastWriteTimeUtc)
            {
                return null;
            }

            var temporaryFile = $"{file}.tmp-{Guid.NewGuid():N}";
            try
            {
                await using (var stream = new FileStream(
                    temporaryFile,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 16 * 1024,
                    options: FileOptions.Asynchronous))
                {
                    for (var index = 0; index < events.Count; index++)
                    {
                        // 序列号重排为 1..N：压缩后的日志从快照事件重新起算。
                        await stream.WriteAsync(
                            Serialize(events[index].WithSequence(index + 1L)), ct).ConfigureAwait(false);
                    }

                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }

                File.Move(temporaryFile, file, overwrite: true);
                var written = new FileInfo(file);
                return new SessionEventLogStamp(
                    sessionId,
                    written.Length,
                    new DateTimeOffset(written.LastWriteTimeUtc, TimeSpan.Zero));
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

    private async Task<IReadOnlyList<SessionEvent>> ReadCoreAsync(
        SessionId sessionId,
        CancellationToken ct)
    {
        var file = GetEventFile(sessionId);
        if (!File.Exists(file))
            return [];

        List<string> lines = [];
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
            catch (JsonException ex)
            {
                // 中段坏行：跳过并上报——一条坏行不再废掉整个会话（列表、加载、后续追加全不可用）。
                // 投影层（EventSourcedSessionStore 的前缀比对）会把缺口自愈为 MessagesReplaced。
                _logger?.LogWarning(ex,
                    "Session {SessionId} event log has a corrupted line at index {Index}; skipping it",
                    sessionId, index);
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
                    item.Metadata), _jsonOptions),
            UserMessageEvent item => JsonSerializer.SerializeToElement(item.Message, _jsonOptions),
            SystemMessageEvent item => JsonSerializer.SerializeToElement(item.Message, _jsonOptions),
            AssistantMessageEvent item => JsonSerializer.SerializeToElement(item.Message, _jsonOptions),
            ToolResultEvent item => JsonSerializer.SerializeToElement(item.Message, _jsonOptions),
            MessagesReplacedEvent item => JsonSerializer.SerializeToElement(
                new MessagesReplacedPayload(
                    item.Messages.Select(SerializeMessage).ToArray(),
                    item.Reason), _jsonOptions),
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
        _ => throw new JsonException($"Unknown message discriminator: {payload.Type}"),
    };

    private string GetEventFile(SessionId sessionId) =>
        Path.Combine(_eventsDirectory, $"{sessionId.Value}.{CoreConstants.Session.EventsFileExtension}");

    /// <summary>会话级跨进程锁文件路径（locks/ 子目录，与事件 JSONL 隔离避免目录枚举噪声）。</summary>
    private string GetLockFile(SessionId sessionId)
    {
        Directory.CreateDirectory(_lockDirectory);
        return Path.Combine(_lockDirectory, $"{sessionId.Value}.lock");
    }

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
        IReadOnlyDictionary<string, object>? Metadata);

    private sealed record MessagesReplacedPayload(
        IReadOnlyList<MessagePayload> Messages,
        string Reason);

    private sealed record MessagePayload(string Type, JsonElement Value);
}
