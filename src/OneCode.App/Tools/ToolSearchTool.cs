using System.ComponentModel;
using OneCode.App.Query;

namespace OneCode.App.Tools;

/// <summary>
/// ToolSearch — search for available tools by keyword.
/// 排序由 <see cref="ToolMetadataRegistry.SearchTools"/> 的统一检索索引给出
/// （分词 + 字段加权 + IDF），取代旧的 +3/+2/+1 子串计分。
/// </summary>
/// <remarks>
/// 链路二（ToolSearch 激活）：当模型使用 <c>select:X</c> 精确查找时，
/// 自动将目标工具激活到当前会话的 <see cref="SessionToolSet"/> 中。
/// 激活通过 <see cref="ISessionToolSetManager.TryActivate"/> → <see cref="ToolActivationContext"/> 完成。
/// </remarks>
public sealed class ToolSearchTool
{
    private const int DefaultMaxResults = 5;
    private const int MaxResultsCap = 20;
    private const string SelectPrefix = "select:";

    private readonly ToolMetadataRegistry _metadata;
    private readonly ISessionToolSetManager _activationManager;

    public ToolSearchTool(ToolMetadataRegistry metadata, ISessionToolSetManager activationManager)
    {
        _metadata = metadata;
        _activationManager = activationManager;
    }

    [Description("Search for available tools by keyword or select a specific tool with 'select:<name>'.")]
    public ToolResult Search(
        [Description("Keywords or 'select:<tool_name>' for direct lookup.")] string query,
        [Description("Max results (default 5, max 20).")] int maxResults = DefaultMaxResults)
    {
        var capabilities = ToolActivationContext.CurrentCapabilities;
        var allNames = _metadata.GetVisibleToolNames()
            .Where(name => capabilities?.AllowedToolNames.Contains(name) == true)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        maxResults = Math.Clamp(maxResults, 1, MaxResultsCap);

        if (query.StartsWith(SelectPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var target = query[SelectPrefix.Length..].Trim();
            var found = allNames.Where(n => n.Equals(target, StringComparison.OrdinalIgnoreCase))
                .Take(1).Select(n => BuildInfo(n, activated: TryActivateAndReport(n))).ToList();
            return ToolResult.JsonSuccess(new { matches = found, query, total_tools = allNames.Count });
        }

        var ranked = _metadata.SearchTools(query, MaxResultsCap)
            .Where(m => allNames.Contains(m.ToolName))
            .Take(maxResults)
            .Select(m => BuildInfo(m.ToolName, activated: false))
            .ToList();

        return ToolResult.JsonSuccess(new { matches = ranked, query, total_tools = allNames.Count });
    }

    /// <summary>
    /// 尝试激活工具并返回激活状态（链路二）。
    /// </summary>
    private bool TryActivateAndReport(string toolName)
    {
        return _activationManager.TryActivate(toolName);
    }

    private object BuildInfo(string name, bool activated = false)
    {
        var meta = _metadata.Get(name);
        return new
        {
            name,
            description = meta?.SearchHint ?? "",
            isReadOnly = meta?.Risk is ToolRisk.Safe or ToolRisk.ReadOnly,
            isDestructive = meta?.Risk == ToolRisk.Destructive || meta?.Risk == ToolRisk.Dynamic,
            isConcurrencySafe = meta?.IsConcurrencySafe ?? true,
            aliases = meta?.Aliases ?? [],
            activated,
        };
    }
}
