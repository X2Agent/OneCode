using Microsoft.Extensions.Hosting;
using OneCode.App.Session;

namespace OneCode.App.Services.Hooks;

/// <summary>
/// 宿主停止时关闭前台会话的兜底（原 SessionEndHookService——SessionEnd 已随 Hook 拦截点重构
/// 移出 Hook 范围，本类不再触发任何 hook，仅保证会话收尾）。
/// 覆盖 TUI 退出键 / Ctrl+C / 宿主关闭等未经 /exit 命令的退出路径；
/// /exit、/quit 已由 ExitCommand 以 prompt_input_exit 显式关闭会话，此处幂等 no-op。
/// </summary>
public sealed class HostStopSessionCloseService(
    ISessionManager sessionManager,
    ILogger<HostStopSessionCloseService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>宿主停止时关闭前台会话。使用 None 而非停止令牌——停止阶段令牌可能已取消，不能截断收尾投递。</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        try
        {
            await sessionManager.CloseAsync(SessionEndReason.Other, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Session close on host stopping failed");
        }
    }
}
