namespace OneCode.App.Services.Hooks;

/// <summary>
/// 钩子注册表——管理所有钩子处理器
///
/// 架构特点：
/// - 按 matcher 分组索引，支持事件 + matcher 两维过滤
/// - Priority 升序排序执行（数值越小越先执行）
/// - 仅负责注册、查询、移除；执行调度由 HookExecutionService 负责
/// </summary>
public sealed class HookRegistry
{
    private readonly GlobHookMatcher _matcher;
    private readonly Dictionary<HookEvent, List<MatcherGroup>> _matcherIndex = new();
    private readonly object _lock = new();

    public HookRegistry(GlobHookMatcher matcher)
    {
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
    }

    public void Register(HookRegistration hook)
    {
        lock (_lock)
        {
            if (!_matcherIndex.TryGetValue(hook.Event, out var groups))
            {
                groups = [];
                _matcherIndex[hook.Event] = groups;
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
    /// 获取匹配指定事件和 matcher 值的 hook 注册项。
    /// 使用 _matcherIndex 进行 O(1) 事件查找，避免 GetAll() 的 O(n) 全量扫描。
    /// </summary>
    public IReadOnlyList<HookRegistration> GetMatchesForEvent(HookEvent @event, string? matcherValue)
        => GetMatchesLocked(@event, matcherValue);

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

    private List<HookRegistration> GetMatchesLocked(HookEvent @event, string? matcherValue)
    {
        lock (_lock)
        {
            if (!_matcherIndex.TryGetValue(@event, out var groups))
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
