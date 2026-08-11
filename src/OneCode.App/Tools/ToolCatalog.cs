using Microsoft.Extensions.AI;
using OneCode.Infrastructure.Mcp;
using System.Reflection;

namespace OneCode.App.Tools;

/// <summary>
/// Aggregates registered tools into an <see cref="AIFunction"/> list and merges live MCP tools.
/// Does not hold <see cref="IServiceProvider"/> — resolution happens in the composition-root
/// factory that builds the <see cref="Lazy{T}"/> passed to this constructor.
/// </summary>
public sealed class ToolCatalog : IToolCatalog
{
    private readonly Lazy<List<AIFunction>> _staticTools;
    private readonly IMcpConnectionManager? _mcpConnectionManager;
    private readonly Lock _mcpMetadataLock = new();

    /// <summary>
    /// Creates a catalog whose static tools are resolved lazily by the composition root
    /// (or tests). The lazy factory must not close over business types that form a DI cycle
    /// at construction time — only at first <see cref="Tools"/> access.
    /// </summary>
    public ToolCatalog(
        Lazy<List<AIFunction>> staticTools,
        ToolMetadataRegistry metadata,
        IMcpConnectionManager? mcpConnectionManager)
    {
        _staticTools = staticTools;
        Metadata = metadata;
        _mcpConnectionManager = mcpConnectionManager;
    }

    /// <summary>
    /// Test/composition helper: builds a catalog that resolves tools from registrations
    /// via <paramref name="services"/> on first <see cref="Tools"/> access.
    /// </summary>
    public static ToolCatalog FromRegistrations(
        IServiceProvider services,
        ToolMetadataRegistry metadata,
        IEnumerable<ToolRegistration> registrations,
        IMcpConnectionManager? mcpConnectionManager = null)
    {
        var regs = registrations.ToList();
        // Lazy owns the IServiceProvider capture — composition root / test boundary only.
        var staticTools = new Lazy<List<AIFunction>>(
            () => BuildStaticTools(services, regs, metadata),
            LazyThreadSafetyMode.ExecutionAndPublication);
        return new ToolCatalog(staticTools, metadata, mcpConnectionManager);
    }

    public ToolMetadataRegistry Metadata { get; }

    public IReadOnlyList<AIFunction> Tools
    {
        get
        {
            var tools = new List<AIFunction>(_staticTools.Value);
            AddMcpTools(tools);
            return tools.AsReadOnly();
        }
    }

    public AIFunction? Find(string name)
        => Tools.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public IReadOnlySet<string> GetVisibleToolNames()
        => Metadata.GetVisibleToolNames();

    private void AddMcpTools(List<AIFunction> tools)
    {
        if (_mcpConnectionManager is null)
            return;

        var names = new HashSet<string>(tools.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var tool in _mcpConnectionManager.GetAllTools())
        {
            if (!names.Add(tool.Name))
                continue;

            tools.Add(tool);
            lock (_mcpMetadataLock)
            {
                Metadata.Register(new ToolMetadata
                {
                    Name = tool.Name,
                    Risk = ToolRisk.Dynamic,
                    ApprovalMode = ToolApprovalMode.Conditional,
                    IsConcurrencySafe = false,
                    SearchHint = $"MCP tool: {tool.Description}",
                    LoadPolicy = ToolLoadPolicy.Contextual,
                    Keywords = [tool.Name, "mcp"],
                });
            }
        }
    }

    /// <summary>
    /// Builds AIFunctions from registrations. Called only from composition-root Lazy factories
    /// or tests — never from business construction paths that still need an unresolved ChatService.
    /// </summary>
    internal static List<AIFunction> BuildStaticTools(
        IServiceProvider services,
        IReadOnlyList<ToolRegistration> registrations,
        ToolMetadataRegistry metadata)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tools = new List<AIFunction>();

        foreach (var reg in registrations)
        {
            if (!names.Add(reg.Name))
                throw new InvalidOperationException($"Duplicate tool registration: {reg.Name}");

            var function = CreateFunction(services, reg);
            if (function is null)
                continue; // DI 中未注册的工具静默跳过（如平台条件工具）

            tools.Add(function);
            metadata.Register(new ToolMetadata
            {
                Name = reg.Name,
                Aliases = reg.Aliases ?? [],
                Risk = reg.Risk,
                ApprovalMode = reg.ApprovalMode ?? ToolPolicyDefaults.ForRisk(reg.Risk),
                IsConcurrencySafe = reg.Concurrency,
                IsVisible = reg.Visible,
                SearchHint = reg.SearchHint,
                LoadPolicy = reg.LoadPolicy,
                Keywords = reg.Keywords ?? [],
                Category = reg.Category,
            });
        }

        // Initialize ToolNames facade with the populated registry so that
        // PermissionCheckHelpers / PermissionProfiles can query tool categories
        // without maintaining duplicate hardcoded lists.
        ToolNames.Initialize(metadata);

        return tools;
    }

    private static AIFunction? CreateFunction(IServiceProvider services, ToolRegistration reg)
    {
        // 1. 工厂模式（AddToolInstance 注册的特殊工具）
        if (reg.FunctionFactory is { } factory)
            return factory(services);

        // 2. 反射模式（AddTool / AddToolStatic 注册的标准工具）
        if (reg.ServiceType is { } type && reg.MethodName is { } methodName)
        {
            var bindingFlags = reg.IsStatic
                ? BindingFlags.Public | BindingFlags.Static
                : BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            var methodInfo = type.GetMethod(methodName, bindingFlags)
                ?? throw new InvalidOperationException($"Tool method not found: {type.Name}.{methodName}");

            object? target = null;
            if (!reg.IsStatic)
            {
                target = reg.InstanceFactory?.Invoke(services);
                if (target is null)
                    return null; // DI 中未注册，静默跳过
            }

            return AIFunctionFactory.Create(methodInfo, name: reg.Name, target: target);
        }

        throw new InvalidOperationException($"Invalid ToolRegistration: {reg.Name} — neither FunctionFactory nor ServiceType/MethodName set");
    }
}
