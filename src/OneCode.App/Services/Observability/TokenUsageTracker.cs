using OneCode.Core.Cost;

namespace OneCode.App.Services.Observability;

/// <summary>
/// Token 使用量统计——CostTracker 的派生视图。
///
/// Token 计数（Input/Output/CacheRead/CacheWrite）从 CostTracker 的 SessionCostInfo 读取，
/// 避免双账本。仅保留 CostTracker 不具备的维度：QueryCount、CacheHitRate、CalibrationFactor、LastBreakdown。
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
public sealed class TokenUsageTracker : ITokenUsageTracker
{
    private readonly ICostTracker _costTracker;
    private readonly ISessionIdProvider _sessionIdProvider;
    private readonly object _gate = new();
    private int _queryCount;
    private TokenBreakdown? _lastBreakdown;

    private const int MinSamplesForCalibration = 2;
    private const int MaxCalibrationSamples = 10;
    private readonly Queue<double> _calibrationRatios = new();
    private double _calibrationFactor = 1.0;

    // Fallback accumulators (used when CostTracker or sessionId is not available)
    private long _fallbackInputTokens;
    private long _fallbackOutputTokens;
    private long _fallbackCacheReadTokens;
    private long _fallbackCacheWriteTokens;

    public TokenUsageTracker(
        ICostTracker costTracker,
        ISessionIdProvider sessionIdProvider)
    {
        _costTracker = costTracker;
        _sessionIdProvider = sessionIdProvider;
    }

    private SessionCostInfo? SessionInfo
    {
        get
        {
            var sessionId = _sessionIdProvider.CurrentSessionId;
            if (sessionId is null)
                return null;
            return _costTracker.GetSessionCost(sessionId.Value);
        }
    }

    public long TotalInputTokens
    {
        get
        {
            if (SessionInfo is { } si) return si.TotalInputTokens;
            lock (_gate) return _fallbackInputTokens;
        }
    }

    public long TotalOutputTokens
    {
        get
        {
            if (SessionInfo is { } si) return si.TotalOutputTokens;
            lock (_gate) return _fallbackOutputTokens;
        }
    }

    public long TotalCacheReadTokens
    {
        get
        {
            if (SessionInfo is { } si) return si.TotalCacheReadTokens;
            lock (_gate) return _fallbackCacheReadTokens;
        }
    }

    public long TotalCacheWriteTokens
    {
        get
        {
            if (SessionInfo is { } si) return si.TotalCacheWriteTokens;
            lock (_gate) return _fallbackCacheWriteTokens;
        }
    }

    public int QueryCount { get { lock (_gate) return _queryCount; } }

    public TokenBreakdown? LastBreakdown { get { lock (_gate) return _lastBreakdown; } }

    public double CalibrationFactor { get { lock (_gate) return _calibrationFactor; } }

    public long TotalAllTokens =>
        TotalInputTokens + TotalOutputTokens + TotalCacheWriteTokens;

    public double CacheHitRate =>
        TotalInputTokens > 0 ? (double)TotalCacheReadTokens / TotalInputTokens : 0;

    public void Record(TokenUsage? usage, TokenBreakdown? breakdown = null)
    {
        if (usage is null) return;

        lock (_gate)
        {
            // Fallback accumulation — only when sessionId is not available (no active session)
            var sessionId = _sessionIdProvider.CurrentSessionId;
            if (sessionId is null)
            {
                _fallbackInputTokens += usage.InputTokens;
                _fallbackOutputTokens += usage.OutputTokens;
                _fallbackCacheReadTokens += usage.CacheReadTokens;
                _fallbackCacheWriteTokens += usage.CacheWriteTokens;
            }

            _queryCount++;
            if (breakdown is not null)
                _lastBreakdown = breakdown;

            if (breakdown is not null && breakdown.TotalEstimated > 0 && usage.InputTokens > 0)
            {
                var ratio = (double)usage.InputTokens / breakdown.TotalEstimated;
                _calibrationRatios.Enqueue(ratio);
                while (_calibrationRatios.Count > MaxCalibrationSamples)
                    _calibrationRatios.Dequeue();

                if (_calibrationRatios.Count >= MinSamplesForCalibration)
                    _calibrationFactor = _calibrationRatios.Average();
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _fallbackInputTokens = 0;
            _fallbackOutputTokens = 0;
            _fallbackCacheReadTokens = 0;
            _fallbackCacheWriteTokens = 0;
            _lastBreakdown = null;
            _queryCount = 0;
            _calibrationRatios.Clear();
            _calibrationFactor = 1.0;
        }
    }
}
