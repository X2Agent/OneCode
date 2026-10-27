using OneCode.Core.Domain;
using OneCode.Core.Session;
using InfraConstants = OneCode.Infrastructure.Config.Constants;

namespace OneCode.Infrastructure.Persistence;

/// <summary>
/// 会话摘要索引（<c>~/.onecode/events/summaries/</c> 下的 JSON 小文件）。
/// 冷启动只读这些几十字节的索引，不再为拿标题/条数解析整份事件日志；
/// 日志戳不匹配即视为未命中，由调用方回退解析并回填。
/// </summary>
public sealed class FileSessionSummaryIndex : ISessionSummaryIndex
{
    private readonly string _indexDirectory;
    private readonly ILogger<FileSessionSummaryIndex> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public FileSessionSummaryIndex(string basePath, ILogger<FileSessionSummaryIndex> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        _logger = logger;
        _indexDirectory = Path.Combine(
            basePath,
            InfraConstants.App.ConfigDirName,
            InfraConstants.Subdirs.Events,
            InfraConstants.Subdirs.SessionSummaries);
        Directory.CreateDirectory(_indexDirectory);
    }

    /// <inheritdoc />
    public async Task<ConversationSummary?> ReadAsync(
        SessionEventLogStamp stamp,
        CancellationToken ct = default)
    {
        var path = GetIndexFile(stamp.SessionId);
        if (!File.Exists(path))
            return null;

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            var entry = await JsonSerializer
                .DeserializeAsync<SummaryIndexEntry>(stream, _jsonOptions, ct)
                .ConfigureAwait(false);
            if (entry is null || entry.LogLength != stamp.Length || entry.LogLastWriteTimeUtc != stamp.LastWriteTimeUtc)
                return null;

            return new ConversationSummary(
                stamp.SessionId,
                entry.Title,
                entry.Model,
                entry.MessageCount,
                entry.CreatedAt,
                entry.LastActivityAt,
                new TokenUsage(entry.InputTokens, entry.OutputTokens, entry.CacheReadTokens, entry.CacheWriteTokens),
                entry.Mode);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "会话摘要索引读取失败，回退解析事件日志：{Path}", path);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task WriteAsync(
        SessionEventLogStamp stamp,
        ConversationSummary summary,
        CancellationToken ct = default)
    {
        var path = GetIndexFile(stamp.SessionId);
        var temporary = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            var entry = new SummaryIndexEntry(
                summary.Id.Value,
                summary.Title,
                summary.Model,
                summary.MessageCount,
                summary.CreatedAt,
                summary.LastActivityAt,
                summary.TotalUsage.InputTokens,
                summary.TotalUsage.OutputTokens,
                summary.TotalUsage.CacheReadTokens,
                summary.TotalUsage.CacheWriteTokens,
                summary.Mode,
                stamp.Length,
                stamp.LastWriteTimeUtc);

            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, entry, _jsonOptions, ct).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            // 保存路径带业务取消令牌：丢弃索引即可，不得让已经落盘的会话保存失败。
            _logger.LogDebug("会话摘要索引写入被取消：{Path}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 索引只是加速层：写失败最多让下一次多解析一份日志，不得影响会话保存。
            _logger.LogWarning(ex, "会话摘要索引写入失败：{Path}", path);
        }
        finally
        {
            TryDeleteTemporary(temporary);
        }
    }

    /// <summary>清理临时文件：残留的 <c>.tmp-*</c> 只是垃圾，清理失败同样不得逃逸。</summary>
    private void TryDeleteTemporary(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "会话摘要索引临时文件清理失败：{Path}", path);
        }
    }

    /// <inheritdoc />
    public Task RemoveAsync(SessionId sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var path = GetIndexFile(sessionId);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "会话摘要索引删除失败：{SessionId}", sessionId);
        }

        return Task.CompletedTask;
    }

    private string GetIndexFile(SessionId sessionId) =>
        Path.Combine(_indexDirectory, $"{sessionId.Value}.json");

    private sealed record SummaryIndexEntry(
        string Id,
        string? Title,
        string Model,
        int MessageCount,
        DateTimeOffset CreatedAt,
        DateTimeOffset LastActivityAt,
        int InputTokens,
        int OutputTokens,
        int CacheReadTokens,
        int CacheWriteTokens,
        string? Mode,
        long LogLength,
        DateTimeOffset LogLastWriteTimeUtc);
}
