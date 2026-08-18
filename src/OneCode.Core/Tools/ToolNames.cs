namespace OneCode.Core.Tools;

/// <summary>
/// 工具名称分类查询——元数据驱动的单一事实源。
///
/// 在应用启动时通过 <see cref="Initialize(ToolMetadataRegistry)"/> 注入注册表引用，
/// 之后所有查询委托给 <see cref="ToolMetadataRegistry"/>。
/// 工具分类在注册时通过 <see cref="ToolCategory"/> 声明，取代硬编码 HashSet。
/// </summary>
public static class ToolNames
{
    private static ToolMetadataRegistry? _registry;
    private static int _initialized;

    /// <summary>初始化——在 DI 容器构建后、工具注册完成后调用。仅接受第一次调用（first-writer-wins）。</summary>
    internal static void Initialize(ToolMetadataRegistry registry)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
            _registry = registry;
    }

    private static ToolMetadataRegistry Registry =>
        _registry ?? throw new InvalidOperationException(
            "ToolNames has not been initialized. Call ToolNames.Initialize(registry) at startup.");

    /// <summary>只读工具判断——基于 ToolMetadata.Risk == ReadOnly。</summary>
    public static bool IsReadOnlyTool(string? toolName)
        => Registry.IsReadOnlyTool(toolName);

    /// <summary>通过单一 filePath 参数编辑文件的工具（Write / Edit）。</summary>
    public static bool IsFileEditTool(string? toolName)
        => Registry.IsInCategory(toolName, ToolCategory.FileEdit);

    /// <summary>会修改文件内容的工具（Write / Edit / ApplyWorkspaceEdit）。</summary>
    public static bool IsFileWriteTool(string? toolName)
        => Registry.IsInCategory(toolName, ToolCategory.FileWrite);

    /// <summary>Plan 模式下允许的工具（超出 ReadOnly 范围）。</summary>
    public static bool IsPlanAllowedTool(string? toolName)
        => Registry.IsInCategory(toolName, ToolCategory.PlanAllowed);

    /// <summary>按分类查询工具（通用入口，供安全不变量等基础设施按需扩展类别）。</summary>
    public static bool IsInCategory(string? toolName, ToolCategory category)
        => Registry.IsInCategory(toolName, category);

    /// <summary>只读工具集合（快照）。</summary>
    public static IReadOnlySet<string> ReadOnlyTools => Registry.GetReadOnlyToolNames();

    /// <summary>文件编辑工具集合（快照）。</summary>
    public static IReadOnlySet<string> FileEditTools => Registry.GetToolNamesByCategory(ToolCategory.FileEdit);

    /// <summary>文件写入工具集合（快照）。</summary>
    public static IReadOnlySet<string> FileWriteTools => Registry.GetToolNamesByCategory(ToolCategory.FileWrite);

    /// <summary>Plan 模式允许工具集合（快照）。</summary>
    public static IReadOnlySet<string> PlanAllowedTools => Registry.GetToolNamesByCategory(ToolCategory.PlanAllowed);
}
