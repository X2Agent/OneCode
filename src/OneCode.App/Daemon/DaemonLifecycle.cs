using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using OneCode.Infrastructure;

namespace OneCode.App.Daemon;

/// <summary>daemon 运行状态快照（状态文件内容，供 /daemon 展示）。</summary>
public sealed record DaemonStatus(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("startedAt")] long StartedAtUnix,
    [property: JsonPropertyName("projectDir")] string ProjectDir);

/// <summary>
/// daemon 与 TUI 间的生命周期协作面：项目 slug、单实例互斥体、状态文件与停止哨兵。
/// 一个 daemon 进程绑定一个项目（进程工作目录）。**存活判定以互斥体为真相**——状态文件
/// 只作信息展示，进程崩溃可能残留；探测方不得以状态文件的存在推断运行中。
/// </summary>
public static class DaemonLifecycle
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    /// <summary>daemon 状态目录：<c>~/.onecode/daemon</c>。</summary>
    public static string GetStateDirectory()
        => Path.Combine(PathsHelper.GetUserConfigDir(), "daemon");

    /// <summary>
    /// 项目 slug：目录名（仅保留安全字符）+ 路径哈希前 8 位。人可读（安装脚本任务名、
    /// 状态文件名）且全局唯一（同名目录不同路径不碰撞）。
    /// </summary>
    public static string GetSlug(string projectDir)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(projectDir))))[..8].ToLowerInvariant();
        var name = new string(Path.GetFileName(Path.GetFullPath(projectDir.TrimEnd('/', '\\')))
            .Where(char.IsAsciiLetterOrDigit)
            .ToArray());
        return name.Length == 0 ? $"daemon-{hash}" : $"{name}-{hash}";
    }

    /// <summary>单实例互斥体名（会话内可见；Task Scheduler 登录任务与用户终端同会话）。</summary>
    public static string GetMutexName(string projectDir)
        => $@"Local\OneCode.Daemon.{GetSlug(projectDir)}";

    /// <summary>状态文件路径：<c>~/.onecode/daemon/{slug}.json</c>。</summary>
    public static string GetStatusFilePath(string projectDir)
        => Path.Combine(GetStateDirectory(), $"{GetSlug(projectDir)}.json");

    /// <summary>停止哨兵路径：<c>~/.onecode/daemon/{slug}.stop</c>；出现即请求 daemon 停止。</summary>
    public static string GetStopFilePath(string projectDir)
        => Path.Combine(GetStateDirectory(), $"{GetSlug(projectDir)}.stop");

    /// <summary>
    /// 探测项目对应的 daemon 是否在运行。命名互斥体随最后一个句柄关闭被 OS 销毁，
    /// 故「互斥体存在」≈「持有进程存活」（崩溃进程不会再阻塞后续启动）。
    /// </summary>
    public static bool IsRunning(string projectDir)
    {
        try
        {
            using var _ = Mutex.OpenExisting(GetMutexName(projectDir));
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    /// <summary>写入运行状态快照（启动时调用；失败吞咽——状态文件只是展示面，不阻断启动）。</summary>
    public static void WriteStatus(string projectDir)
    {
        try
        {
            var status = new DaemonStatus(
                Environment.ProcessId,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Path.GetFullPath(projectDir));
            Directory.CreateDirectory(GetStateDirectory());
            File.WriteAllText(GetStatusFilePath(projectDir), JsonSerializer.Serialize(status, s_jsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 展示面写失败不影响 daemon 职责。
        }
    }

    /// <summary>读取状态快照；无文件或损坏返回 null（调用方以 <see cref="IsRunning"/> 为准）。</summary>
    public static DaemonStatus? TryReadStatus(string projectDir)
        => ReadStatusFile(GetStatusFilePath(projectDir));

    /// <summary>解析状态文件；缺失 / 损坏返回 null 而非抛出——展示面不得因残留损坏文件崩溃。</summary>
    internal static DaemonStatus? ReadStatusFile(string statusFilePath)
    {
        try
        {
            return File.Exists(statusFilePath)
                ? JsonSerializer.Deserialize<DaemonStatus>(File.ReadAllText(statusFilePath))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>清除状态快照（退出时调用；崩溃残留由下次启动覆盖）。</summary>
    public static void ClearStatus(string projectDir)
    {
        try
        {
            var path = GetStatusFilePath(projectDir);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 残留状态文件无害：探测方以互斥体为准。
        }
    }

    /// <summary>写停止哨兵（TUI 侧 /daemon stop 调用）；daemon 的 <see cref="DaemonStopWatcher"/> 消费。</summary>
    public static void RequestStop(string projectDir)
    {
        Directory.CreateDirectory(GetStateDirectory());
        File.WriteAllText(GetStopFilePath(projectDir), string.Empty);
    }
}
