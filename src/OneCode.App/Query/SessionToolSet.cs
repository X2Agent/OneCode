using Microsoft.Extensions.AI;

namespace OneCode.App.Query;

/// <summary>
/// 会话级工具激活状态——每个会话持有一份，管理 Contextual/Deferred 工具的激活生命周期。
/// </summary>
/// <remarks>
/// 核心设计原则：
/// <list type="bullet">
///   <item><b>单调递增</b>：工具一旦激活就不会被移除，保证 prompt 前缀稳定（prompt caching 友好）。</item>
///   <item><b>追加不重排</b>：新激活的工具追加到列表末尾，不改变已有工具的相对顺序。</item>
///   <item><b>三条激活链路</b>：
///     <list type="number">
///       <item>初始选择：prompt 经索引评分，超阈值的 Contextual 工具自动并入。</item>
///       <item>ToolSearch 激活：<c>select:X</c> 或搜索结果触发 <see cref="Activate"/>。</item>
///       <item>未知工具兜底：模型 hallucinate 的工具名若在注册表中存在，自动激活。</item>
///     </list>
///   </item>
/// </list>
/// 线程安全：所有公共方法通过 <see cref="Lock"/> 同步。
/// </remarks>
public sealed class SessionToolSet
{
    private readonly IToolCatalog _catalog;
    private readonly ToolMetadataRegistry _metadata;
    private readonly List<string> _activatedOrder = [];      // 激活顺序（追加不重排）
    private readonly HashSet<string> _activatedSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public SessionToolSet(IToolCatalog catalog, ToolMetadataRegistry metadata)
    {
        _catalog = catalog;
        _metadata = metadata;
    }

    /// <summary>当前已激活的工具名称集合（只读快照）。</summary>
    public IReadOnlySet<string> ActivatedNames
    {
        get
        {
            lock (_lock)
                return _activatedSet.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 返回当前会话可用的工具列表：<see cref="ToolLoadPolicy.Always"/> 工具 + 已激活工具。
    /// 首次或 prompt 变化时，自动将评分超阈值的 Contextual 工具并入激活集。
    /// </summary>
    /// <remarks>
    /// 返回顺序：Always 工具在前（按 catalog 注册顺序），已激活工具在后（按激活时间顺序）。
    /// 这个顺序在会话内保持稳定——新工具只追加到末尾，不重排已有工具。
    /// </remarks>
    public IReadOnlyList<AIFunction> GetTools(string userPrompt)
        => GetTools(userPrompt, ToolCapabilitySet.CreateUnrestricted(_catalog.Tools.Select(tool => tool.Name)));

    public IReadOnlyList<AIFunction> GetTools(string userPrompt, ToolCapabilitySet capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        lock (_lock)
        {
            // 链路一：初始选择——只允许激活当前 run 能力边界内的 Contextual 工具。
            if (capabilities.AllowDynamicActivation && !string.IsNullOrWhiteSpace(userPrompt))
            {
                var matched = _metadata.SelectToolsForLocalModel(userPrompt);
                foreach (var name in matched.Where(name => capabilities.AllowedToolNames.Contains(name)))
                {
                    if (_activatedSet.Add(name))
                        _activatedOrder.Add(name);
                }
            }

            var result = new List<AIFunction>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Always 工具（catalog 注册顺序）
            foreach (var tool in _catalog.Tools)
            {
                var meta = _metadata.Get(tool.Name);
                if (meta is not { LoadPolicy: ToolLoadPolicy.Always }
                    || !capabilities.AllowedToolNames.Contains(tool.Name))
                    continue;

                result.Add(tool);
                seen.Add(tool.Name);
            }

            // 已激活工具（激活时间顺序），跳过已是 Always 的
            foreach (var name in _activatedOrder)
            {
                if (seen.Contains(name) || !capabilities.AllowedToolNames.Contains(name))
                    continue;

                var tool = _catalog.Find(name);
                if (tool is null)
                    continue;

                result.Add(tool);
                seen.Add(name);
            }

            return result;
        }
    }

    /// <summary>
    /// 显式激活一个工具（链路二/三）。工具名必须在注册表中存在且可见。
    /// </summary>
    /// <returns>true 表示新激活；false 表示已激活或工具不存在。</returns>
    public bool Activate(string toolName)
        => Activate(toolName, ToolCapabilitySet.CreateUnrestricted(_catalog.Tools.Select(tool => tool.Name)));

    public bool Activate(string toolName, ToolCapabilitySet capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!capabilities.AllowDynamicActivation || !capabilities.AllowedToolNames.Contains(toolName))
            return false;

        var meta = _metadata.Get(toolName);
        if (meta is null || !meta.IsVisible || !meta.IsEnabled)
            return false;

        lock (_lock)
        {
            // Always 工具无需激活
            if (meta.LoadPolicy == ToolLoadPolicy.Always)
                return false;

            if (_activatedSet.Add(toolName))
            {
                _activatedOrder.Add(toolName);
                return true;
            }
            return false;
        }
    }
}
