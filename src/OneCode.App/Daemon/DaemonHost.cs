using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OneCode.App.Logging;
using OneCode.App.Services;
using OneCode.Core.Models;
using OneCode.Infrastructure.Agent;

namespace OneCode.App.Daemon;

/// <summary>
/// OneCode 无头守护进程宿主：核心组合根（不挂 TUI 注册段）承载后台服务驻留——
/// cron 常驻触发、模型目录刷新等全部经既有 hosted services 生效。
/// 一个 daemon 进程绑定一个项目（进程工作目录），与 TUI 并存时 durable cron
/// 由逐作业触发锁仲裁。启动协议为 CLI 动词 <c>daemon</c>（/daemon start 与 OS
/// 调度器注册拉起的是同一条命令），不作为用户功能入口宣传。
/// </summary>
public static class DaemonHost
{
    /// <summary>daemon 已在运行的退出码（区别于正常退出 0 与错误 1）。</summary>
    public const int ExitCodeAlreadyRunning = 3;

    public static async Task<int> RunAsync(CancellationToken ct = default)
    {
        var workingDir = Environment.CurrentDirectory;

        // 单实例守卫：互斥体同时是 /daemon 探针的存活真相，必须在宿主构建前获取。
        using var mutex = new Mutex(
            initiallyOwned: true, DaemonLifecycle.GetMutexName(workingDir), out var createdNew);
        if (!createdNew)
        {
            var existing = DaemonLifecycle.TryReadStatus(workingDir);
            Console.Error.WriteLine(
                $"OneCode daemon 已在运行（{(existing is null ? "另一实例" : $"PID {existing.Pid}")}，项目 {workingDir}）。");
            return ExitCodeAlreadyRunning;
        }

        var builder = Host.CreateApplicationBuilder();
        OneCodeApp.AddOneCodeCore(builder.Services, workingDir, configureFrontend: null);
        builder.ConfigureApplicationLogging();

        var host = builder.Build();
        // 遥测监听须在 agent 首次运行前挂上：立即解析一次转发器，订阅 MAF ActivitySource。
        _ = host.Services.GetRequiredService<AgentTelemetryLogForwarder>();
        _ = host.Services.GetRequiredService<IModelCatalogCache>();

        await host.StartAsync(ct).ConfigureAwait(false);
        await host.Services.GetRequiredService<AppStartupService>()
            .WarmUpAsync(ct).ConfigureAwait(false);

        DaemonLifecycle.WriteStatus(workingDir);
        using var stopWatcher = new DaemonStopWatcher(
            DaemonLifecycle.GetStopFilePath(workingDir), () => _ = StopHostAsync(host));

        Console.WriteLine($"OneCode daemon 已启动（PID {Environment.ProcessId} · 项目 {workingDir}）");
        Console.WriteLine("停止：Ctrl+C，或 TUI 内 /daemon stop");

        try
        {
            // Console lifetime：Ctrl+C / SIGTERM 触发优雅停止；停止哨兵走 StopHostAsync 同一出口。
            await host.WaitForShutdownAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            DaemonLifecycle.ClearStatus(workingDir);
            host.Dispose();
        }

        return 0;
    }

    private static async Task StopHostAsync(IHost host)
    {
        try
        {
            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 哨兵触发的停止失败只能吞咽：宿主的 Ctrl+C 路径仍可用作兜底。
            Console.Error.WriteLine($"daemon 停止失败：{ex.Message}");
        }
    }
}
