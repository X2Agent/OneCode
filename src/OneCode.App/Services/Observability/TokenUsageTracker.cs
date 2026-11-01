using OneCode.Core.Tokens;

namespace OneCode.App.Services.Observability;

/// <summary>
/// Token 使用量统计——TokenLedger 的派生视图。
///
/// Token 计数（Input/Output/CacheRead/CacheWrite）从 ITokenLedger 的 SessionTokenUsage 读取，
/// 避免双账本。仅保留账本不具备的维度：QueryCount、CacheHitRate、CalibrationFactor、LastBreakdown。
///
/// sessionId 通过 <see cref="ISessionIdProvider"/> 延迟解析——SessionManager 实现该接口，
/// 打破 SessionManager ↔ TokenUsageTracker 的循环依赖。
///
/// MEAI 契约（UsageDetails）：
///   - InputTokens：完整输入 token 数（已含缓存命中部分）。
///   - CacheReadTokens：其中缓存命中的子集。
///   - CacheWriteTokens：缓存写入部分（Anthropic 创生）。
///   - ReasoningTokens：推理 token（是 OutputTokens 的子集）。
///
/// 缓存命中率 = CacheReadTokens / InputTokens
/// </summary>
public sealed class TokenUsageTracker(ITokenLedger tokenLedger, ISessionIdProvider sessionIdProvider) : ITokenUsageTracker
{
    private readonly ITokenLedger _tokenLedger = tokenLedger;
    private readonly ISessionIdProvider _sessionIdProvider = sessionIdProvider;
    private readonly object _gate = new();
    private int _queryCount;
    private TokenBreakdown? _lastBreakdown;

    private const int MinSamplesForCalibration = 2;
    private const int MaxCalibrationSamples = 10;
    private readonly Queue<double> _calibrationRatios = new();
    private double _calibrationFactor = 1.0;
    private int _lastTurnInputTokens;
    private BreakdownAccumulator _cumulative = new();

    // 无会话 id 时的兜底累加器（有会话时一律读账本会话桶）
    private SessionTokenUsage _fallback = new();

    private SessionTokenUsage? SessionInfo
    {
        get
        {
            var sessionId = _sessionIdProvider.CurrentSessionId;
            if (sessionId is null)
                return null;
            return _tokenLedger.GetSessionUsage(sessionId.Value);
        }
    }

    public long TotalInputTokens => Read(static usage => usage.TotalInputTokens);
    public long TotalOutputTokens => Read(static usage => usage.TotalOutputTokens);
    public long TotalCacheReadTokens => Read(static usage => usage.TotalCacheReadTokens);
    public long TotalCacheWriteTokens => Read(static usage => usage.TotalCacheWriteTokens);

    private long Read(Func<SessionTokenUsage, long> selector)
        => selector(SessionInfo ?? _fallback);

    public int QueryCount { get { lock (_gate) return _queryCount; } }

    public TokenBreakdown? LastBreakdown { get { lock (_gate) return _lastBreakdown; } }

    public int LastTurnInputTokens { get { lock (_gate) return _lastTurnInputTokens; } }

    public TokenBreakdown? CumulativeBreakdown { get { lock (_gate) return _cumulative.ToBreakdown(); } }

    public double CalibrationFactor { get { lock (_gate) return _calibrationFactor; } }

    public long TotalAllTokens =>
        TotalInputTokens + TotalOutputTokens + TotalCacheWriteTokens;

    public double CacheHitRate =>
        TotalInputTokens > 0 ? (double)TotalCacheReadTokens / TotalInputTokens : 0;

    public void Record(TokenUsage? usage, TokenBreakdown? breakdown = null, int lastTurnInputTokens = 0)
    {
        if (usage is null) return;

        lock (_gate)
        {
            // 无会话 id 时账本不落桶，退回本地累加
            if (_sessionIdProvider.CurrentSessionId is null)
                _fallback.Add(usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheWriteTokens);

            if (lastTurnInputTokens > 0)
                _lastTurnInputTokens = lastTurnInputTokens;

            _queryCount++;

            if (breakdown is null)
                return;

            _cumulative.Add(breakdown);
            _lastBreakdown = breakdown;

            if (breakdown.TotalEstimated <= 0 || usage.InputTokens <= 0)
                return;

            _calibrationRatios.Enqueue((double)usage.InputTokens / breakdown.TotalEstimated);
            while (_calibrationRatios.Count > MaxCalibrationSamples)
                _calibrationRatios.Dequeue();

            if (_calibrationRatios.Count >= MinSamplesForCalibration)
                _calibrationFactor = _calibrationRatios.Average();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _fallback = new();
            _lastBreakdown = null;
            _lastTurnInputTokens = 0;
            _queryCount = 0;
            _calibrationRatios.Clear();
            _calibrationFactor = 1.0;
            _cumulative = new();
        }
    }

    /// <summary>分项会话累计：每查询的估算分量逐项相加（ContextProviders 按 sourceId 归并）。</summary>
    private sealed class BreakdownAccumulator
    {
        private readonly Dictionary<string, long> _providers = new(StringComparer.Ordinal);
        private long _systemPrompt;
        private long _productTools;
        private long _mcpTools;
        private long _messages;
        private long _other;
        private bool _any;

        public void Add(TokenBreakdown breakdown)
        {
            _any = true;
            _systemPrompt += breakdown.SystemPrompt;
            _productTools += breakdown.ProductTools;
            _mcpTools += breakdown.McpTools;
            _messages += breakdown.Messages;
            _other += breakdown.Other;
            foreach (var (sourceId, tokens) in breakdown.ContextProviders)
                _providers[sourceId] = _providers.GetValueOrDefault(sourceId) + tokens;
        }

        public TokenBreakdown? ToBreakdown()
        {
            if (!_any)
                return null;

            var providers = _providers.ToDictionary(kv => kv.Key, kv => ToInt(kv.Value));
            var total = _systemPrompt + _productTools + _mcpTools + _messages + _other
                + _providers.Values.Sum();

            return new TokenBreakdown(
                ToInt(_systemPrompt),
                ToInt(_productTools),
                ToInt(_mcpTools),
                ToInt(_messages),
                providers,
                ToInt(_other),
                ToInt(total));
        }

        private static int ToInt(long value) => (int)Math.Clamp(value, 0, int.MaxValue);
    }
}
