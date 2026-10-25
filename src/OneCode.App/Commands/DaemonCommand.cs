using System.Text;
using OneCode.App.Daemon;
using OneCode.Automation.Cron;
using OneCode.Infrastructure;

namespace OneCode.App.Commands;

/// <summary>
/// /daemon —— TUI 内的 daemon 管理面：查看运行状态与本项目 cron 概况、显式启停。
/// daemon 是绑定本项目（进程工作目录）的无头宿主，与 TUI 并存时 durable cron 由触发锁仲裁；
/// 开机常驻注册（OS 调度器）不在本命令内，走 scripts/install-daemon.ps1。
/// </summary>
public sealed class DaemonCommand(CronSchedulerService cronScheduler) : Command
{
    private const string InstallHint = "scripts/install-daemon.ps1";

    /// <summary>daemon 启动就绪等待上限（宿主构建需数秒，超时≠失败）。</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);

    /// <summary>优雅停止等待上限；超时只报告不强杀（daemon 可能在收尾长任务）。</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);

    public override string Name => "daemon";
    public override string Description => "Manage the project daemon (status/start/stop)";
    public override CommandCategory Category => CommandCategory.Builtin;
    public override string? ArgumentHint => "[status|start|stop]";

    public override Task<CommandResult> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0 || args[0].Equals("status", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Status());

        return args[0].ToLowerInvariant() switch
        {
            "start" => StartAsync(ct),
            "stop" => StopAsync(ct),
            _ => Task.FromResult(CommandResult.Error("Usage: /daemon [status|start|stop]")),
        };
    }

    private CommandResult Status()
    {
        var projectDir = Environment.CurrentDirectory;
        var running = DaemonLifecycle.IsRunning(projectDir);
        var status = DaemonLifecycle.TryReadStatus(projectDir);

        var sb = new StringBuilder();
        if (running)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"daemon 运行中（PID {PidText(status)} · 启动于 {FormatUnix(status?.StartedAtUnix)} · 项目 {projectDir}）");
        }
        else
        {
            sb.AppendLine("daemon 未运行——durable cron 作业仅在 onecode 进程（TUI/daemon）存活期间触发。");
            sb.AppendLine($"  本会话即用：/daemon start；开机常驻：{InstallHint}");
        }

        var durable = cronScheduler.GetJobs().Where(static j => j.Durable).ToList();
        if (durable.Count == 0)
        {
            sb.Append("本项目无 durable cron 作业（/cron create <cron> <prompt> --durable 创建）");
        }
        else
        {
            long? next = durable
                .Where(static j => j is { Paused: false, NextRunAt: not null })
                .Select(static j => (long?)j.NextRunAt!.Value)
                .Min();
            sb.Append(CultureInfo.InvariantCulture,
                $"本项目 durable cron 作业 {durable.Count} 个，下次触发 {FormatUnix(next)}；完整列表见 /cron");
        }

        return CommandResult.Text(sb.ToString());
    }

    private async Task<CommandResult> StartAsync(CancellationToken ct)
    {
        var projectDir = Environment.CurrentDirectory;
        if (DaemonLifecycle.IsRunning(projectDir))
        {
            var existing = DaemonLifecycle.TryReadStatus(projectDir);
            return CommandResult.Text(
                $"daemon 已在运行（PID {PidText(existing)}），无需重复启动。");
        }

        var exe = Environment.ProcessPath;
        if (exe is null)
            return CommandResult.Error("无法定位 onecode 可执行文件（Environment.ProcessPath 为空）。");

        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--cwd");
            psi.ArgumentList.Add(projectDir);
            psi.ArgumentList.Add("daemon");
            using var _ = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start 返回 null");
        }
        catch (Exception ex)
        {
            return CommandResult.Error(
                $"daemon 启动失败：{ex.Message}。可在项目目录手动运行 `onecode daemon` 排查。");
        }

        var logDir = Path.Combine(PathsHelper.GetUserConfigDir(), "debug");
        var deadline = DateTimeOffset.UtcNow + StartTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
            if (!DaemonLifecycle.IsRunning(projectDir)) continue;

            var status = DaemonLifecycle.TryReadStatus(projectDir);
            return CommandResult.Text(
                $"daemon 已启动（PID {PidText(status)} · 项目 {projectDir}）。\n" +
                $"  日志目录：{logDir}");
        }

        return CommandResult.Text(
            "daemon 启动中（5 秒内未就绪——宿主构建较慢或启动失败）。\n" +
            $"  稍后 /daemon 查看状态；日志目录：{logDir}");
    }

    private async Task<CommandResult> StopAsync(CancellationToken ct)
    {
        var projectDir = Environment.CurrentDirectory;
        if (!DaemonLifecycle.IsRunning(projectDir))
            return CommandResult.Text("daemon 未在运行。");

        DaemonLifecycle.RequestStop(projectDir);

        var deadline = DateTimeOffset.UtcNow + StopTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
            if (!DaemonLifecycle.IsRunning(projectDir))
                return CommandResult.Text("daemon 已停止。");
        }

        return CommandResult.Text(
            "停止请求已发出，但 daemon 15 秒内未退出（可能在收尾长任务）。稍后用 /daemon 确认。");
    }

    private static string PidText(DaemonStatus? status) =>
        status?.Pid.ToString(CultureInfo.InvariantCulture) ?? "?";

    private static string FormatUnix(long? unixSeconds) => unixSeconds is null
        ? "-"
        : DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value).LocalDateTime
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
