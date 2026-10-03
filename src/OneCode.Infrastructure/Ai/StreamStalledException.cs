namespace OneCode.Infrastructure.Ai;

/// <summary>
/// 模型流式响应停滞：HTTP 连接未断但持续无数据（超过 chunk 间空闲上限）时抛出。
/// HttpClient.Timeout = Infinite 的流式路径下，TCP 半开/代理停滞表现为"流永不结束也不抛错"，
/// 空响应防线只在流正常结束时触发，覆盖不到这种停滞；本异常把停滞转化为可重试的瞬时错误，
/// 重试耗尽后沿引擎异常路径发错误事件——否则 TUI 将永久显示"执行 &lt;工具&gt;"转圈。
/// </summary>
public sealed class StreamStalledException(string message) : Exception(message);
