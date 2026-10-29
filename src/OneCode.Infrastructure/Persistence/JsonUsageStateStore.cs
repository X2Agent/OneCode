using OneCode.Core.Domain;
using OneCode.Core.Tokens;
using OneCode.Infrastructure.Config;

namespace OneCode.Infrastructure.Persistence;

/// <summary>已持久化的 token 账本状态：进程可计费总量 + 各会话桶。</summary>
/// <param name="TotalBillableTokens">进程级累计（<see cref="UsageMath.BillableTokens(long, long, long, long)"/> 口径，缓存读 10% 折算）。</param>
/// <param name="Sessions">按会话累计的原始 usage 桶（<see cref="SessionTokenUsage"/> 快照）。</param>
public sealed record PersistedTokenUsageState(
    long TotalBillableTokens,
    IReadOnlyDictionary<SessionId, SessionTokenUsage> Sessions);

/// <summary>
/// 批量用量增量：四维原始计数用于会话统计，<paramref name="BillableTokens"/> 是逐条记账时
/// 已按 <see cref="UsageMath.BillableTokens(UsageRecord)"/> 折算的增量，避免聚合后二次截断。
/// </summary>
public sealed record UsageBatchItem(
    SessionId? SessionId,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long BillableTokens);

/// <summary>
/// TokenLedger 的跨进程持久化：每批增量在文件锁内读取最新状态、追加增量并原子替换
/// <c>{home}/.onecode/usage/usage.json</c>，避免多进程整份快照互相覆盖。
/// 单个 .bak 保存上一份有效状态；主文件损坏时从备份恢复，均不可用时显式失败而不把预算清零。
/// </summary>
public sealed class JsonUsageStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly string _usageDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<JsonUsageStateStore>? _logger;

    public JsonUsageStateStore(string basePath, ILogger<JsonUsageStateStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        _logger = logger;
        _usageDirectory = Path.Combine(
            basePath,
            Constants.App.ConfigDirName,
            Constants.Subdirs.Usage);
        _filePath = Path.Combine(_usageDirectory, "usage.json");
        Directory.CreateDirectory(_usageDirectory);
    }

    /// <summary>读取最新持久化状态；文件缺失返回空状态，损坏时恢复有效备份。</summary>
    public PersistedTokenUsageState Load()
    {
        _gate.Wait();
        try
        {
            using var crossProcessLock = DurableFile.AcquireLock(GetLockPath());
            return LoadWithRecovery();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 在一个跨进程事务中合并整批增量：一次锁、一次读取、一次原子写入。
    /// 已删除会话的迟到增量直接跳过；其他会话与全局可计费总量不受影响。
    /// </summary>
    public PersistedTokenUsageState RecordBatch(IReadOnlyList<UsageBatchItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return Mutate(state =>
        {
            var sessions = CopySessions(state.Sessions);
            var billableDelta = 0L;
            foreach (var item in items)
            {
                if (item.SessionId is { } deletedId
                    && SessionDeletionGuard.IsDeleted(_usageDirectory, deletedId))
                {
                    continue;
                }

                if (item.SessionId is { } id)
                {
                    if (!sessions.TryGetValue(id, out var bucket))
                    {
                        bucket = new SessionTokenUsage();
                        sessions[id] = bucket;
                    }
                    bucket.Add(
                        item.InputTokens,
                        item.OutputTokens,
                        item.CacheReadTokens,
                        item.CacheWriteTokens);
                }
                billableDelta = checked(billableDelta + item.BillableTokens);
            }

            return new PersistedTokenUsageState(
                checked(state.TotalBillableTokens + billableDelta),
                sessions);
        });
    }

    /// <summary>在跨进程事务中删除会话桶并写穿，防止重启后恢复已删除数据。</summary>
    public PersistedTokenUsageState RemoveSession(SessionId sessionId)
    {
        SessionDeletionGuard.MarkDeleted(_usageDirectory, sessionId);
        return Mutate(state =>
        {
            var sessions = CopySessions(state.Sessions);
            sessions.Remove(sessionId);
            return state with { Sessions = sessions };
        });
    }

    private PersistedTokenUsageState Mutate(
        Func<PersistedTokenUsageState, PersistedTokenUsageState> update)
    {
        _gate.Wait();
        try
        {
            using var crossProcessLock = DurableFile.AcquireLock(GetLockPath());
            var updated = update(LoadWithRecovery());
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new PersistedUsageStateDocument(
                    1,
                    updated.TotalBillableTokens,
                    updated.Sessions.ToDictionary(
                        pair => pair.Key.Value,
                        pair => new SessionUsageDocument(
                            pair.Value.TotalInputTokens,
                            pair.Value.TotalOutputTokens,
                            pair.Value.TotalCacheReadTokens,
                            pair.Value.TotalCacheWriteTokens))),
                JsonOptions);
            DurableFile.WriteAtomic(_filePath, payload, canBackupSource: CanReadState);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private PersistedTokenUsageState LoadWithRecovery()
    {
        if (!File.Exists(_filePath))
            return File.Exists(GetBackupPath()) ? ReadState(GetBackupPath()) : EmptyState();

        try
        {
            return ReadState(_filePath);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException or OverflowException)
        {
            if (File.Exists(GetBackupPath()))
            {
                try
                {
                    var restored = ReadState(GetBackupPath());
                    _logger?.LogWarning(ex, "Token usage state is corrupted; restored the last valid backup");
                    return restored;
                }
                catch (Exception backupEx) when (backupEx is JsonException or InvalidDataException or FormatException or OverflowException)
                {
                    throw new InvalidDataException("Token usage state and its backup are both invalid.", backupEx);
                }
            }

            throw new InvalidDataException("Token usage state is invalid and no valid backup is available.", ex);
        }
    }

    private static PersistedTokenUsageState ReadState(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schema_version", out var version)
            || version.ValueKind != JsonValueKind.Number
            || version.GetInt32() != 1)
        {
            throw new InvalidDataException("Unsupported token usage state schema.");
        }

        if (!root.TryGetProperty("total_billable_tokens", out var totalElement)
            || totalElement.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidDataException("Token usage state is missing its total.");
        }
        var total = totalElement.GetInt64();
        if (total < 0)
            throw new InvalidDataException("Token usage totals cannot be negative.");

        if (!root.TryGetProperty("sessions", out var sessionsElement)
            || sessionsElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Token usage state is missing its session buckets.");
        }

        var sessions = new Dictionary<SessionId, SessionTokenUsage>();
        foreach (var property in sessionsElement.EnumerateObject())
        {
            if (SessionId.TryParse(property.Name) is not { } sessionId)
                throw new InvalidDataException($"Invalid session ID '{property.Name}' in token usage state.");
            if (property.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("A token usage session bucket must be an object.");

            var bucket = new SessionTokenUsage();
            bucket.Add(
                ReadCount(property.Value, "input_tokens"),
                ReadCount(property.Value, "output_tokens"),
                ReadCount(property.Value, "cache_read_tokens"),
                ReadCount(property.Value, "cache_write_tokens"));
            sessions.Add(sessionId, bucket);
        }

        return new PersistedTokenUsageState(total, sessions);
    }

    private static long ReadCount(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidDataException($"Token usage bucket is missing '{propertyName}'.");
        }

        var raw = value.GetInt64();
        if (raw < 0)
            throw new InvalidDataException("Token usage bucket values cannot be negative.");
        return raw;
    }

    private static Dictionary<SessionId, SessionTokenUsage> CopySessions(
        IReadOnlyDictionary<SessionId, SessionTokenUsage> sessions)
        => sessions.ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot());

    private static PersistedTokenUsageState EmptyState()
        => new(0, new Dictionary<SessionId, SessionTokenUsage>());

    private static bool CanReadState(string path)
    {
        try
        {
            _ = ReadState(path);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private string GetBackupPath() => _filePath + ".bak";
    private string GetLockPath() => _filePath + ".lock";

    private sealed record PersistedUsageStateDocument(
        int SchemaVersion,
        long TotalBillableTokens,
        IReadOnlyDictionary<string, SessionUsageDocument> Sessions);

    private sealed record SessionUsageDocument(
        long InputTokens,
        long OutputTokens,
        long CacheReadTokens,
        long CacheWriteTokens);
}
