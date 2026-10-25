using System.Collections.Concurrent;
using OneCode.App.Services.Interaction;

namespace OneCode.WebUI.Services;

/// <summary>
/// Web 前端的交互投递通道：按浏览器电路注册接收者，投递给「活跃电路」——
/// 即最近一次初始化或提交查询的电路（用户正在操作的窗口）。
/// 未注册任何接收者时 <see cref="TrySend"/> 返回 false，发送方走无头降级。
/// </summary>
/// <remarks>
/// 同一进程内多个电路共享同一引擎会话：交互请求（审批/提问/预算）只能由一个窗口应答，
/// 由活跃电路独占，避免后开的标签页静默抢占、或某个电路销毁时误清其他电路的接收者。
/// </remarks>
public sealed class WebInteractionDispatcher : IInteractionDispatcher
{
    private readonly ConcurrentDictionary<string, Action<InteractionRequest>> _receivers = new();
    // 活跃电路：全部读写经 Interlocked / Volatile，不声明 volatile（避免 CS0420）。
    private string? _activeCircuitId;

    /// <summary>注册电路的交互接收者。</summary>
    public void Register(string circuitId, Action<InteractionRequest> receiver)
    {
        ArgumentException.ThrowIfNullOrEmpty(circuitId);
        ArgumentNullException.ThrowIfNull(receiver);

        _receivers[circuitId] = receiver;
    }

    /// <summary>注销电路接收者；若该电路为活跃电路，交还给剩余电路（仅剩一个时）或清空。</summary>
    public void Unregister(string circuitId)
    {
        _receivers.TryRemove(circuitId, out _);

        if (Interlocked.CompareExchange(ref _activeCircuitId, null, circuitId) == circuitId)
        {
            // 活跃电路离线：仅剩一个电路时无缝交还，多于一个时等待用户操作（MarkActive）再路由。
            var remaining = _receivers.Keys.ToList();
            if (remaining.Count == 1)
                Volatile.Write(ref _activeCircuitId, remaining[0]);
        }
    }

    /// <summary>标记活跃电路（电路初始化或提交查询时调用）。</summary>
    public void MarkActive(string circuitId)
    {
        if (_receivers.ContainsKey(circuitId))
            Volatile.Write(ref _activeCircuitId, circuitId);
    }

    /// <summary>向活跃电路投递交互请求；无活跃电路时不投递并返回 false。</summary>
    public bool TrySend(InteractionRequest request)
    {
        var activeId = Volatile.Read(ref _activeCircuitId);
        if (activeId is null || !_receivers.TryGetValue(activeId, out var receiver))
            return false;

        receiver(request);
        return true;
    }
}
