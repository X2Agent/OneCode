using OneCode.Infrastructure;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Services.AutoDream;

/// <summary>
/// AutoDream 的会话文件扫描器：统计指定时间后的新会话数、判定会话文件归属项目。
/// 拥有全局配置目录与日志依赖，从 <see cref="AutoDreamService"/> 分离出纯扫描职责。
/// </summary>
/// <remarks>
/// 会话文件位于 <c>~/.onecode/events/</c>（见 <see cref="Constants.Subdirs.Events"/>），
/// 由 <c>FileSessionEventStore</c> 以「一条事件一行」的 JSONL 写出。
/// 首行是 <c>SessionStarted</c> / <c>SessionSnapshot</c> 事件信封，项目路径在其
/// <c>payload.working_directory</c> 字段（信封为 snake_case）。
/// </remarks>
internal sealed class AutoDreamSessionScanner(ILogger logger, string globalConfigDir)
{
    public int CountNewSessionsSince(DateTimeOffset since, string? projectRoot)
    {
        var sessionsDir = Path.Combine(globalConfigDir, Constants.Subdirs.Events);
        if (!Directory.Exists(sessionsDir)) return 0;

        if (projectRoot is null)
        {
            logger.LogDebug("AutoDream: workingDirectory not available, skipping session count");
            return 0;
        }

        try
        {
            var files = Directory.GetFiles(sessionsDir, "*.jsonl");
            var count = 0;
            foreach (var file in files)
            {
                if (File.GetLastWriteTimeUtc(file) <= since.UtcDateTime)
                    continue;

                if (IsSessionForProject(file, projectRoot))
                    count++;
            }
            return count;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to count new sessions in {SessionsDir}", sessionsDir);
            return 0;
        }
    }

    /// <summary>
    /// 检查会话文件是否属于当前项目：读取 JSONL 首行（会话元数据事件），
    /// 取 <c>payload.working_directory</c> 并比较。
    /// </summary>
    public bool IsSessionForProject(string sessionFile, string projectRoot)
    {
        try
        {
            var firstLine = ReadFirstLine(sessionFile);
            if (string.IsNullOrEmpty(firstLine))
                return false;

            using var doc = JsonDocument.Parse(firstLine);
            var wd = ExtractWorkingDirectory(doc.RootElement);
            if (string.IsNullOrWhiteSpace(wd))
                return false;

            return string.Equals(
                PathsHelper.NormalizePath(wd),
                PathsHelper.NormalizePath(projectRoot),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read working_directory from {File}", sessionFile);
            return false;
        }
    }

    /// <summary>从事件信封的 <c>payload.working_directory</c> 取项目路径。</summary>
    private static string? ExtractWorkingDirectory(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (!root.TryGetProperty("payload", out var payload) ||
            payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return payload.TryGetProperty("working_directory", out var wd) &&
               wd.ValueKind == JsonValueKind.String
            ? wd.GetString()
            : null;
    }

    private static string? ReadFirstLine(string file)
    {
        using var stream = File.OpenRead(file);
        using var reader = new StreamReader(stream);
        return reader.ReadLine();
    }
}
