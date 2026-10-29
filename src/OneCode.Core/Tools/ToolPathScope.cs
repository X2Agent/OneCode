namespace OneCode.Core.Tools;

/// <summary>工具对文件系统的触达范围，决定安全层套用哪一组路径规则。</summary>
public enum ToolPathScopeKind
{
    /// <summary>不触达文件系统（含 provider 注入的会话状态工具与未注册工具）：全部路径检查跳过。</summary>
    None,

    /// <summary>只读触达：敏感读保护 + 工作区边界。</summary>
    ReadOnly,

    /// <summary>写入/删除触达：写保护 + 工作区边界。</summary>
    Write,

    /// <summary>动态工具（MCP/第三方服务器）：敏感读保护 + 工作区边界，不套写保护。</summary>
    Dynamic,
}

/// <summary>
/// 工具路径触达范围的单一判定来源：全部结论来自 <see cref="ToolNames"/> 的元数据，
/// 不再维护「工具名 → 是否文件工具」的硬编码集合。
/// 安全不变量（Layer 0）与权限边界校验（Layer 1）必须共用本类，禁止各自另写一套判定。
/// </summary>
public static class ToolPathScope
{
    /// <summary>写入类分类：调用会修改工作区内容。</summary>
    private const ToolCategory WriteCategories =
        ToolCategory.FileEdit | ToolCategory.FileWrite | ToolCategory.FileDelete;

    /// <summary>
    /// 判定顺序固定为 Write → ReadOnly → Dynamic → None：
    /// 文件编辑工具同时也是 Destructive，必须先命中写分类；<c>Dynamic</c> 只覆盖 MCP 这类
    /// 运行期注册、未声明文件分类的工具（见 <c>ToolCatalog.AddMcpTools</c>）。
    /// </summary>
    public static ToolPathScopeKind Classify(string? toolName)
    {
        if (string.IsNullOrEmpty(toolName))
            return ToolPathScopeKind.None;

        if (ToolNames.IsInCategory(toolName, WriteCategories))
            return ToolPathScopeKind.Write;

        if (ToolNames.IsReadOnlyTool(toolName))
            return ToolPathScopeKind.ReadOnly;

        if (ToolNames.IsDynamicTool(toolName))
            return ToolPathScopeKind.Dynamic;

        return ToolPathScopeKind.None;
    }

    /// <summary>该范围是否需要校验路径（<see cref="ToolPathScopeKind.None"/> 之外都需要）。</summary>
    public static bool TouchesFiles(ToolPathScopeKind scope) => scope != ToolPathScopeKind.None;

    /// <summary>该范围是否套用敏感读保护（凭证类文件不可读）。</summary>
    public static bool RequiresSensitiveReadCheck(ToolPathScopeKind scope) =>
        scope is ToolPathScopeKind.ReadOnly or ToolPathScopeKind.Dynamic;

    /// <summary>该范围是否套用写保护（.git/.ssh/.env 等不可写）。</summary>
    public static bool RequiresWriteCheck(ToolPathScopeKind scope) => scope == ToolPathScopeKind.Write;
}
