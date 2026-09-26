namespace OneCode.App.Services.Hooks;

/// <summary>
/// 钩子注册表——管理所有钩子处理器
///
/// 架构特点：
/// - 按 matcher 分组索引，支持拦截点 + matcher 两维过滤
/// - Priority 升序排序执行（数值越小越先执行）
/// - 仅负责注册、查询、移除；执行调度由 HookExecutionService 负责
/// - <see cref="Generation"/> 记录配置代次：整体替换时递增，供可观测性与审计引用
/// </summary>
public sealed class HookRegistry(GlobHookMatcher matcher)
{
    private readonly GlobHookMatcher _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
    private readonly Dictionary<HookInterceptionPoint, List<MatcherGroup>> _matcherIndex = new();
    private readonly object _lock = new();
    private long _generation;

    /// <summary>当前配置代次；每次整体替换 +1，单调递增。</summary>
    public long Generation
    {
        get { lock (_lock) { return _generation; } }
    }

    public void Register(HookRegistration hook)
    {
        lock (_lock)
        {
            RegisterLocked(hook);
        }
    }

    /// <summary>
    /// 原子整体替换注册表内容（hook 配置热重载专用）：
    /// 清空现有索引后重新注册传入的 hook，全程持有 _lock——
    /// 读取方要么看到完整旧快照、要么看到完整新快照，不会看到中间态。
    /// </summary>
    public void ReplaceAll(IEnumerable<HookRegistration> hooks)
    {
        lock (_lock)
        {
            _matcherIndex.Clear();
            foreach (var hook in hooks)
                RegisterLocked(hook);
            _generation++;
        }
    }

    private void RegisterLocked(HookRegistration hook)
    {
        if (!_matcherIndex.TryGetValue(hook.Point, out var groups))
        {
            groups = [];
            _matcherIndex[hook.Point] = groups;
        }

        var group = groups.Find(g =>
            string.Equals(g.Pattern, hook.Matcher ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        if (group is null)
        {
            group = new MatcherGroup(hook.Matcher ?? string.Empty);
            groups.Add(group);
        }

        group.Hooks.Add(hook);
    }

    public IReadOnlyList<HookRegistration> GetAll()
    {
        lock (_lock)
        {
            return _matcherIndex.Values
                .SelectMany(g => g)
                .SelectMany(g => g.Hooks)
                .ToList();
        }
    }

    /// <summary>
    /// 获取匹配指定拦截点和 matcher 值的 hook 注册项。
    /// 使用 _matcherIndex 进行 O(1) 拦截点查找，避免 GetAll() 的 O(n) 全量扫描。
    /// </summary>
    public IReadOnlyList<HookRegistration> GetMatchesForPoint(HookInterceptionPoint point, string? matcherValue)
        => GetMatchesLocked(point, matcherValue);

    public void Unregister(string name)
    {
        lock (_lock)
        {
            foreach (var groups in _matcherIndex.Values)
            {
                foreach (var group in groups)
                {
                    group.Hooks.RemoveAll(h =>
                        string.Equals(h.Name, name, StringComparison.Ordinal));
                }
                groups.RemoveAll(g => g.Hooks.Count == 0);
            }
        }
    }

    private List<HookRegistration> GetMatchesLocked(HookInterceptionPoint point, string? matcherValue)
    {
        lock (_lock)
        {
            if (!_matcherIndex.TryGetValue(point, out var groups))
                return [];

            List<HookRegistration> matchedHooks = [];
            foreach (var group in groups)
            {
                if (_matcher.Matches(group.Pattern, matcherValue ?? string.Empty))
                {
                    matchedHooks.AddRange(group.Hooks);
                }
            }

            return matchedHooks;
        }
    }

    private sealed class MatcherGroup
    {
        public string Pattern { get; }
        public List<HookRegistration> Hooks { get; } = new();

        public MatcherGroup(string pattern)
        {
            Pattern = pattern;
        }
    }
}
