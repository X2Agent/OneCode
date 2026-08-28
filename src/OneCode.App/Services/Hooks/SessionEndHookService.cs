using Microsoft.Extensions.Hosting;
using OneCode.App.Session;

namespace OneCode.App.Services.Hooks;

/// <summary>
/// SessionEnd 退出覆盖兜底（A7）——宿主停止时补发 SessionEnd（reason=other）。
/// 覆盖 TUI 退出键 / Ctrl+C / 宿主关闭等未经 /exit 命令的退出路径；
/// /exit、/quit 已由 ExitCommand 以 prompt_input_exit 显式关闭会话，此处幂等 no-op。
/// </summary>
public sealed class SessionEndHookService(
    ISessionManager sessionManager,
    ILogger<SessionEndHookService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>宿主停止时补发 SessionEnd。使用 None 而非停止令牌——停止阶段令牌可能已取消，不能截断通知投递。</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        try
        {
            await sessionManager.CloseAsync(SessionEndReason.Other, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SessionEnd hook on host stopping failed");
        }
    }
}
