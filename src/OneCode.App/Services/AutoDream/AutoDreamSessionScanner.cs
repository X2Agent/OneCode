using OneCode.Infrastructure;

namespace OneCode.App.Services.AutoDream;

/// <summary>
/// AutoDream 的会话文件扫描器：统计指定时间后的新会话数、判定会话文件归属项目。
/// 拥有全局配置目录与日志依赖，从 <see cref="AutoDreamService"/> 分离出纯扫描职责。
/// </summary>
internal sealed class AutoDreamSessionScanner(ILogger logger, string globalConfigDir)
{
    public int CountNewSessionsSince(DateTimeOffset since, string? projectRoot)
    {
        var sessionsDir = Path.Combine(globalConfigDir, "sessions");
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
    /// 检查会话文件是否属于当前项目：读取 JSONL 首行的 <c>working_directory</c> 字段并比较。
    /// </summary>
    public bool IsSessionForProject(string sessionFile, string projectRoot)
    {
        try
        {
            var firstLine = ReadFirstLine(sessionFile);
            if (string.IsNullOrEmpty(firstLine))
                return false;

            using var doc = JsonDocument.Parse(firstLine);
            if (!doc.RootElement.TryGetProperty("working_directory", out var wdElem))
                return false;

            var wd = wdElem.GetString();
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

    private static string? ReadFirstLine(string file)
    {
        using var stream = File.OpenRead(file);
        using var reader = new StreamReader(stream);
        return reader.ReadLine();
    }
}
