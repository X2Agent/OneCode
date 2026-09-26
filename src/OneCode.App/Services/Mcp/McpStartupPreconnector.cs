using OneCode.Core.Mcp;

namespace OneCode.App.Services.Mcp;

/// <summary>
/// MCP 启动预连接协调器（Plan B）：把 MCP 全量连接从系统提示词构建链移到后台，
/// TUI 不再同步等待握手（此前阻塞首屏直至全部服务器连接完成或超时）。
/// <list type="bullet">
/// <item>交互路径：trust 通过后 <see cref="StartBackground"/> fire-and-forget；</item>
/// <item>首条消息：<see cref="WaitForFirstMessageAsync"/> 对进行中的握手做 ≤5s 有界收尾，超时放行（未就绪服务器的工具下一轮附挂）；</item>
/// <item>cron 等非交互路径：<see cref="EnsureConnectedAsync"/> 显式等待连接完成后再构建提示词。</item>
/// </list>
/// 启动动作经 Lazy + Interlocked 恰好执行一次（并发首调不产生双重连接）；
/// 连接本身的幂等去重由 <see cref="IMcpConnectionManager.ConnectAllAsync"/> 的连接门闩兜底。
/// </summary>
public sealed class McpStartupPreconnector(
    IMcpConnectionManager connectionManager,
    ILogger<McpStartupPreconnector> logger)
{
    /// <summary>首条消息前等待预连接收尾的上限（有界，永不无限阻塞）。</summary>
    private static readonly TimeSpan FirstMessageWaitTimeout = TimeSpan.FromSeconds(5);

    private Lazy<Task>? _preconnect;

    /// <summary>后台启动全量连接（幂等）。返回时连接已在进行中，<paramref name="onCompleted"/> 在连接完成后执行。</summary>
    public void StartBackground(Func<Task>? onCompleted, CancellationToken ct = default)
        => EnsureStartedAsync(onCompleted, ct);

    /// <summary>确保预连接已启动并返回其完成任务（幂等）。cron 等非交互路径用于显式等待连接完成。</summary>
    public Task EnsureConnectedAsync(CancellationToken ct = default)
        => EnsureStartedAsync(onCompleted: null, ct);

    /// <summary>
    /// 首条消息前的有界等待：连接已在后台进行，这里只给进行中的握手收尾时间，
    /// 超时放行。预连接尚未启动时立即返回。
    /// </summary>
    public async Task WaitForFirstMessageAsync(CancellationToken ct = default)
    {
        var preconnect = _preconnect;
        if (preconnect is null) return;
        await Task.WhenAny(preconnect.Value, Task.Delay(FirstMessageWaitTimeout, ct)).ConfigureAwait(false);
    }

    private Task EnsureStartedAsync(Func<Task>? onCompleted, CancellationToken ct)
    {
        var existing = _preconnect;
        if (existing is not null)
            return existing.Value;

        var created = new Lazy<Task>(() => RunAsync(onCompleted, ct));
        // 竞争边界（落败方语义）：Interlocked.CompareExchange 落败方的 onCompleted/ct
        // 被有意丢弃——胜者闭包代表唯一一次后台连接，落败方调用方只是提前拿到同一个
        // 完成任务。当前这是安全的，因为两条调用路径的参数语义可被胜者完整表达：
        //   - StartBackground：trust 通过后仅交互路径调用一次（onCompleted = 技能提供者重建）；
        //   - EnsureConnectedAsync：onCompleted 恒为 null，ct 为应用级生命周期令牌。
        // 若未来出现"后到调用方需要自己的回调/令牌生效"的场景，必须改为登记式
        // （TaskCompletionSource + 回调列表），不得直接复用本方法。
        var winner = Interlocked.CompareExchange(ref _preconnect, created, null) ?? created;
        return winner.Value;
    }

    private async Task RunAsync(Func<Task>? onCompleted, CancellationToken ct)
    {
        try
        {
            await connectionManager.ConnectAllAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // 软失败不中断启动：单服务器失败已由连接层记录（状态栏三态 / /mcp list 可见）。
            logger.LogError(ex, "Background MCP preconnect failed");
            return;
        }

        if (onCompleted is null) return;
        try
        {
            await onCompleted().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 预连接取消，属正常退出。
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MCP post-preconnect callback failed");
        }
    }
}