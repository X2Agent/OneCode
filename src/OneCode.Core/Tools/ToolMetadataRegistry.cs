namespace OneCode.Core.Tools;

/// <summary>
/// 工具加载策略——决定工具何时出现在发送给模型的工具列表中。
/// 由 <see cref="ToolMetadataRegistry.SelectToolsForLocalModel"/> 消费。
/// </summary>
public enum ToolLoadPolicy
{
    /// <summary>始终包含在工具列表中（核心工具）</summary>
    Always,
    /// <summary>仅当用户 prompt 匹配 Keywords/Aliases/Name 时包含</summary>
    Contextual,
    /// <summary>
    /// 不自动加载——只能通过 ToolSearch select 或未知工具兜底显式激活。
    /// 用于低频/高风险工具（如 worktree、cron、browser DevTools），
    /// 避免它们在 prompt 评分中被误触发或占满本地模型的上下文窗口。
    /// </summary>
    Deferred,
}

/// <summary>
/// 工具风险级别。
/// 由权限中间件消费，决定 Plan Mode / AcceptEdits Mode 下的行为。
/// </summary>
public enum ToolRisk
{
    /// <summary>无副作用的安全操作（如 Read, Sleep）</summary>
    Safe,
    /// <summary>只读操作（如 Grep, LS, WebFetch）</summary>
    ReadOnly,
    /// <summary>破坏性操作（如 Write, Edit, Bash "rm"）</summary>
    Destructive,
    /// <summary>由运行时输入决定（如 Bash/PowerShell 根据命令内容判定）</summary>
    Dynamic,
}

/// <summary>
/// 工具分类标签（flags 枚举）。在注册时声明，取代 ToolNames 中的硬编码 HashSet。
/// 一个工具可以同时属于多个分类。
/// </summary>
[Flags]
public enum ToolCategory
{
    None = 0,
    /// <summary>通过单一 filePath 参数编辑文件的工具（Write, Edit）</summary>
    FileEdit = 1,
    /// <summary>会修改文件内容的工具（Write, Edit, ApplyWorkspaceEdit）</summary>
    FileWrite = 2,
    /// <summary>Plan 模式下允许的工具（超出 ReadOnly 范围）</summary>
    PlanAllowed = 4,
}

/// <summary>
/// 工具元数据——存储 MAF AIFunction 不直接支持的额外信息。
/// 这些元数据由权限中间件、Hook 系统、并发调度器消费。
/// </summary>
public sealed record ToolMetadata
{
    /// <summary>工具名称（必须与 AIFunction.Name 一致）</summary>
    public required string Name { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];

    public ToolRisk Risk { get; init; } = ToolRisk.Safe;

    /// <summary>Approval protocol boundary derived from the tool risk by default.</summary>
    public ToolApprovalMode ApprovalMode { get; init; } = ToolApprovalMode.Conditional;

    /// <summary>是否支持并发调用（默认 true）</summary>
    public bool IsConcurrencySafe { get; init; } = true;

    /// <summary>是否对 ToolSearch 等可见（默认 true）</summary>
    public bool IsVisible { get; init; } = true;

    /// <summary>是否启用（可用于平台条件，如 PowerShell 仅 Windows 启用）</summary>
    public bool IsEnabled { get; init; } = true;

    public string? SearchHint { get; init; }

    /// <summary>加载策略：Always、Contextual 或 Deferred</summary>
    public ToolLoadPolicy LoadPolicy { get; init; } = ToolLoadPolicy.Always;

    /// <summary>触发关键词——当 LoadPolicy 为 Contextual 时，prompt 包含任一关键词即加载</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>工具分类标签——在注册时声明，取代 ToolNames 中的硬编码 HashSet</summary>
    public ToolCategory Category { get; init; } = ToolCategory.None;
}

/// <summary>
/// 轻量工具元数据注册表。
/// 只存储元数据，不持有执行逻辑（执行由 AIFunction 直接处理）。
/// </summary>
public sealed class ToolMetadataRegistry
{
    /// <summary>
    /// Contextual 工具的入选评分阈值——约等价于「至少一个较稀有词在 Name/Alias/Keyword 字段精确命中」。
    /// 仅 Hint 字段单 token 命中的弱信号（最高 ≈ idf × 1.0）低于该值，不会仅凭描述把工具加载给本地小模型。
    /// </summary>
    internal const double ContextualScoreThreshold = 5.0;

    private readonly Dictionary<string, ToolMetadata> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToolMetadata> _byAlias = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolRetrievalIndex _index = new();
    private readonly Lock _lock = new();

    /// <summary>注册一个工具的元数据。</summary>
    public void Register(ToolMetadata metadata)
    {
        lock (_lock)
        {
            _byName[metadata.Name] = metadata;
            foreach (var alias in metadata.Aliases)
                _byAlias[alias] = metadata;
            _index.AddOrUpdate(metadata);
        }
    }

    /// <summary>按名称或别名查找元数据。</summary>
    public ToolMetadata? Get(string name)
    {
        lock (_lock)
        {
            return _byName.TryGetValue(name, out var m) ? m
                : _byAlias.TryGetValue(name, out var a) ? a : null;
        }
    }

    public IReadOnlySet<string> GetVisibleToolNames()
    {
        lock (_lock)
        {
            return _byName.Values
                .Where(m => m.IsVisible && m.IsEnabled)
                .Select(m => m.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Returns the centralized policy for a tool.</summary>
    public ToolPolicy GetPolicy(string toolName)
    {
        var metadata = Get(toolName);
        return metadata is null
            ? new ToolPolicy(toolName, ToolRisk.Destructive, ToolApprovalMode.Always, false, false)
            : new ToolPolicy(
                metadata.Name,
                metadata.Risk,
                metadata.ApprovalMode,
                metadata.IsConcurrencySafe,
                metadata.IsVisible);
    }

    /// <summary>Returns whether a tool must cross an approval protocol boundary.</summary>
    public bool RequiresApprovalBoundary(string toolName)
        => GetPolicy(toolName).ApprovalMode is not ToolApprovalMode.Never;

    /// <summary>
    /// 为本地模型（Ollama）选择工具：所有 <see cref="ToolLoadPolicy.Always"/> 工具
    /// + <see cref="ToolLoadPolicy.Contextual"/> 工具中检索评分达到 <see cref="ContextualScoreThreshold"/> 的。
    /// 评分由 <see cref="ToolRetrievalIndex"/> 给出（分词 + 字段加权 + IDF），取代裸子串匹配——
    /// 修复 "planetary" 误命中 "plan"、中文 prompt 无法分词两类问题。
    /// </summary>
    public IReadOnlyList<string> SelectToolsForLocalModel(string userPrompt)
    {
        lock (_lock)
        {
            var result = new List<string>();
            var hasPrompt = !string.IsNullOrWhiteSpace(userPrompt);

            foreach (var meta in _byName.Values)
            {
                if (!meta.IsVisible || !meta.IsEnabled)
                    continue;

                if (meta.LoadPolicy == ToolLoadPolicy.Always)
                {
                    result.Add(meta.Name);
                }
                else if (meta.LoadPolicy == ToolLoadPolicy.Contextual
                         && hasPrompt
                         && _index.Score(meta.Name, userPrompt) >= ContextualScoreThreshold)
                {
                    result.Add(meta.Name);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// 按相关度搜索工具（供 ToolSearch 使用）：仅返回可见且启用的工具，
    /// 按评分降序返回最多 <paramref name="maxResults"/> 条。
    /// </summary>
    public IReadOnlyList<ToolSearchMatch> SearchTools(string query, int maxResults)
    {
        lock (_lock)
        {
            return _index.Search(query)
                .Where(m => _byName.TryGetValue(m.ToolName, out var meta) && meta.IsVisible && meta.IsEnabled)
                .Take(maxResults)
                .ToList();
        }
    }

    /// <summary>清空注册表（用于测试）。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _byName.Clear();
            _byAlias.Clear();
            _index.Clear();
        }
    }

    /// <summary>判断工具是否属于指定分类。</summary>
    public bool IsInCategory(string? toolName, ToolCategory category)
    {
        if (toolName is null) return false;
        lock (_lock)
        {
            return _byName.TryGetValue(toolName, out var meta)
                && (meta.Category & category) != 0;
        }
    }

    /// <summary>判断工具是否为只读工具（Risk == ReadOnly）。</summary>
    public bool IsReadOnlyTool(string? toolName)
    {
        if (toolName is null) return false;
        lock (_lock)
        {
            return _byName.TryGetValue(toolName, out var meta) && meta.Risk == ToolRisk.ReadOnly;
        }
    }

    /// <summary>获取指定分类的所有工具名称。</summary>
    public IReadOnlySet<string> GetToolNamesByCategory(ToolCategory category)
    {
        lock (_lock)
        {
            return _byName.Values
                .Where(m => (m.Category & category) != 0)
                .Select(m => m.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>获取所有 Risk == ReadOnly 的工具名称。</summary>
    public IReadOnlySet<string> GetReadOnlyToolNames()
    {
        lock (_lock)
        {
            return _byName.Values
                .Where(m => m.Risk == ToolRisk.ReadOnly)
                .Select(m => m.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }
}
