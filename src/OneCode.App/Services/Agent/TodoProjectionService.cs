using Microsoft.Agents.AI;
using OneCode.App.Services.Streaming;
using OneCode.Core.Coordinator;

namespace OneCode.App.Services.Agent;

/// <summary>
/// 把 agent 的待办清单快照（Harness <c>TodoProvider</c> 的每会话状态）发布到统一
/// 领域事件总线，供 TUI 渲染——与 <c>PlanCardPublisher</c> 同构的投影通道。
/// </summary>
/// <remarks>
/// <para><b>为什么读快照而不是投影工具事件</b>：<c>todos_*</c> 的 add/complete/remove
/// 都带幂等语义（重复 complete 返回 0、删除不存在的 id 返回 0），从
/// ToolStart/ToolDone 增量重建状态必须自行复刻这套语义，漏一个事件就永久漂移。
/// provider 的 <c>GetAllTodosAsync</c> 是权威全量源，代价是刷新粒度为"轮"——
/// MAF 的 agent 循环在一次 RunStreamingAsync 内跑完整个工具批次，轮间刷新对
/// 交互式 TUI 足够；工具执行中间的实时刷新需要另做评估（增量重建风险见上）。</para>
/// <para><b>解析路径</b>：<c>agent.GetService&lt;TodoProvider&gt;()</c> 依赖 MAF 契约——
/// 每个 <c>DelegatingAIAgent</c> 装饰器都把 GetService 向被包装 agent 转发，
/// 直到 <c>ChatClientAgent</c> 从其 <c>AIContextProviders</c> 中解析
/// （<c>TodoCompletionLoopEvaluator</c> 用的是同一条路径）。解析不到（profile
/// 未启用 Todo）时发布空列表——面板应隐藏，而不是保留上一个 agent 的陈旧清单。</para>
/// </remarks>
public sealed class TodoProjectionService
{
    private readonly OrchestrationEventBus _bus;
    private readonly ILogger<TodoProjectionService> _logger;

    public TodoProjectionService(OrchestrationEventBus bus, ILogger<TodoProjectionService> logger)
    {
        _bus = bus;
        _logger = logger;
    }

    /// <summary>
    /// 读取当前会话的待办快照并发布。无订阅者（headless / Cron）时整体跳过，
    /// 连 provider 查询都不做。
    /// </summary>
    public async Task PublishAsync(AIAgent agent, AgentSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(session);

        if (!_bus.HasSubscribers || ct.IsCancellationRequested)
            return;

        IReadOnlyList<TodoItem> items = [];
        if (agent.GetService<TodoProvider>() is { } provider)
        {
            try
            {
                items = await provider.GetAllTodosAsync(session, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 投影失败不得影响主 run：按"无待办"发布，面板隐藏，下一轮快照自愈。
                _logger.LogWarning(ex, "Failed to read todo snapshot for projection; publishing empty list");
                items = [];
            }
        }

        var projection = new TodoListItem[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            projection[i] = new TodoListItem(item.Id, item.Title, item.Description, item.IsComplete);
        }

        _bus.Publish(new OrchestrationEvent.TodoProjectionChanged(projection));
    }
}
