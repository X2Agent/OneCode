namespace OneCode.Core.Mcp;

/// <summary>
/// MCP 工具白名单过滤器。按服务器配置的 <see cref="McpServerDefinition.EnabledTools"/>
/// 判断该服务器提供的某个方法是否暴露给大模型：
/// <list type="bullet">
///   <item>白名单为 <c>null</c>（未配置）→ 全部放行（向后兼容既有 .mcp.json）。</item>
///   <item>白名单非空 → 名称或通配符模式命中才放行；<c>*</c> 匹配任意字符段。</item>
///   <item>白名单为空列表 → 全部拒绝（连接服务器但不暴露工具，保留 resources 用途）。</item>
/// </list>
/// 仅作用于 MCP 服务器提供的方法——项目内置的 30+ 个静态工具不经过此过滤器。
/// </summary>
public static class McpToolFilter
{
    /// <summary>判断某 MCP 服务器的工具是否在白名单内、应暴露给模型。</summary>
    public static bool IsEnabled(McpServerDefinition definition, string toolName)
        => IsEnabled(definition.EnabledTools, toolName);

    /// <summary>白名单列表重载：便于调用方在只有名单（尚无完整定义）时复用同一匹配语义。</summary>
    public static bool IsEnabled(IReadOnlyList<string>? whitelist, string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        if (whitelist is null)
            return true;

        foreach (var pattern in whitelist)
        {
            if (Matches(pattern, toolName))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 通配符匹配：大小写不敏感，<c>*</c> 匹配任意字符段（如 <c>browser_*</c>、<c>*_screenshot</c>）。
    /// 模式不含 <c>*</c> 时为精确等值匹配。
    /// </summary>
    public static bool Matches(string? pattern, string toolName)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrEmpty(toolName))
            return false;

        const StringComparison Comparison = StringComparison.OrdinalIgnoreCase;
        var firstStar = pattern.IndexOf('*');
        if (firstStar < 0)
            return string.Equals(pattern, toolName, Comparison);

        var lastStar = pattern.LastIndexOf('*');
        var prefix = pattern[..firstStar];
        var suffix = lastStar + 1 < pattern.Length ? pattern[(lastStar + 1)..] : string.Empty;

        if (prefix.Length > 0 && !toolName.StartsWith(prefix, Comparison))
            return false;
        if (suffix.Length > 0 && !toolName.EndsWith(suffix, Comparison))
            return false;
        if (prefix.Length + suffix.Length > toolName.Length)
            return false;

        // 首尾 * 之间的段（可能多个 * 切出的多段）必须按顺序出现在中段区域
        if (lastStar > firstStar)
        {
            var cursor = prefix.Length;
            var suffixStart = toolName.Length - suffix.Length;
            foreach (var segment in pattern[(firstStar + 1)..lastStar].Split('*'))
            {
                if (segment.Length == 0)
                    continue;
                var found = toolName.IndexOf(segment, cursor, Comparison);
                if (found < 0 || found + segment.Length > suffixStart)
                    return false;
                cursor = found + segment.Length;
            }
        }

        return true;
    }
}