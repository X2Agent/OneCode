namespace OneCode.App.Session;

/// <summary>补全弹窗使用的会话候选（短 id 展示 + 完整 id 插入）。</summary>
public sealed record SessionCandidate(
    string FullId,
    string Title,
    string Mode,
    int MessageCount)
{
    public string ShortId => SessionIdResolver.ShortId(FullId);
}

/// <summary>
/// 会话候选快照缓存，供同步补全热路径（<c>/session switch|delete</c> 与 <c>/close</c> 的 id 提示）使用。
/// 补全引擎是同步调用，禁止 sync-over-async——这里同步返回缓存快照，快照陈旧时后台异步刷新，
/// 允许补全提示短时不新鲜。
/// </summary>
public sealed class SessionCandidateProvider
{
    // 30 秒 TTL：补全弹窗是高频同步路径，每次键入都打磁盘不可接受；
    // 而候选仅用于提示展示，插入的是完整 id（命令层仍会校验），30 秒陈旧窗口无副作用。
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly ISessionManager _sessionManager;
    private readonly ILogger<SessionCandidateProvider>? _logger;
    private readonly object _gate = new();
    private IReadOnlyList<SessionCandidate> _snapshot = [];
    private DateTimeOffset _lastRefreshUtc = DateTimeOffset.MinValue;
    private int _refreshInFlight;

    public SessionCandidateProvider(ISessionManager sessionManager, ILogger<SessionCandidateProvider>? logger = null)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    /// <summary>同步返回缓存快照；陈旧时触发一次后台刷新（立即返回旧值）。</summary>
    public IReadOnlyList<SessionCandidate> GetCandidates()
    {
        var stale = DateTimeOffset.UtcNow - _lastRefreshUtc > CacheTtl;
        if (stale)
            ScheduleRefresh();

        lock (_gate)
            return _snapshot;
    }

    private void ScheduleRefresh()
    {
        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0)
            return;

        // 单飞后台刷新：异常全部就地记录，无未观察异常泄漏（补全提示失败可安全忽略）。
        _ = Task.Run(async () =>
        {
            try
            {
                var summaries = await _sessionManager.ListAsync().ConfigureAwait(false);
                var next = summaries
                    .Select(s => new SessionCandidate(
                        s.Id.Value, s.Title ?? "(未命名)", s.Mode ?? "build", s.MessageCount))
                    .ToList();
                lock (_gate)
                {
                    _snapshot = next;
                    _lastRefreshUtc = DateTimeOffset.UtcNow;
                }
            }
            catch (OperationCanceledException)
            {
                // 快照刷新取消：保持旧值，无需处理。
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Session candidate refresh failed; keeping stale snapshot");
            }
            finally
            {
                Interlocked.Exchange(ref _refreshInFlight, 0);
            }
        });
    }
}
