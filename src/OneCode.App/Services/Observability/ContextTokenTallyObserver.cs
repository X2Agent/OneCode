using OneCode.Core;

namespace OneCode.App.Services.Observability;

/// <summary>
/// 消息级 AIContextProvider 注入（harness 待办清单等）的 token 分桶。
///
/// 唯一的可分辨读取点是 <see cref="OneCode.App.Services.Agent.TranscriptChatHistoryProvider"/>
/// 的组装点：消息一旦进入重发路径就会被重打标为历史来源，原始 contributor 消失。同一 provider
/// 一次调用可能注入多条消息，故累加；每次模型调用开始时由 <see cref="BeginModelCall"/> 换代，
/// 避免逐轮重复注入的待办清单累进下一轮。
///
/// 读取走 <see cref="Consume"/>（取走即清空）：<see cref="TokenBreakdownEstimator"/> 在一次查询
/// 结束时恰好消费一次，既保证「本次查询」口径，也不给下一查询留残留。
/// </summary>
public sealed class ContextTokenTallyObserver(ITokenEstimator estimator)
{
    private const string UnknownBucket = "(unknown)";
    private static readonly IReadOnlyDictionary<string, int> Empty = new Dictionary<string, int>();

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _messages = new(StringComparer.Ordinal);

    /// <summary>累加一条注入消息的估算 token（同一 provider 在本轮内累加）。</summary>
    public void AddMessageTokens(string? sourceId, string text)
    {
        var bucket = sourceId ?? UnknownBucket;
        var tokens = estimator.EstimateTokens(text);
        lock (_gate)
            _messages[bucket] = _messages.GetValueOrDefault(bucket) + tokens;
    }

    /// <summary>一次模型调用的组装起点：清掉上一轮的分桶。</summary>
    public void BeginModelCall()
    {
        lock (_gate)
            _messages.Clear();
    }

    /// <summary>取走当前快照并清空（读取即消费，避免跨查询残留）。</summary>
    public IReadOnlyDictionary<string, int> Consume()
    {
        lock (_gate)
        {
            if (_messages.Count == 0)
                return Empty;

            var snapshot = new Dictionary<string, int>(_messages, StringComparer.Ordinal);
            _messages.Clear();
            return snapshot;
        }
    }
}
