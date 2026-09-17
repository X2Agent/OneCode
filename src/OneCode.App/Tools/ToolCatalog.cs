using OneCode.Core.Mcp;
using Microsoft.Extensions.AI;

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
    /// MCP 工具元数据缓存：工具名 → 最近一次注册的 <see cref="ToolMetadata"/>。
    /// <see cref="AddMcpTools"/> 每轮对话都会执行，注册表是覆盖语义的字典 + 检索索引，
    /// 不缓存会对同名工具反复做 Register（含 ToolRetrievalIndex 重建）。
    /// </summary>
    private readonly Dictionary<string, ToolMetadata> _registeredMcpMetadata = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Test/composition helper: builds a catalog that resolves tools from registrations.</summary>
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
            LazyThreadSafetyMode.PublicationOnly);
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
                // 注册表是覆盖语义；同名工具元数据不变，重复 Register 只会白白重建检索索引。
                if (_registeredMcpMetadata.ContainsKey(tool.Name))
                    continue;

                var metadata = new ToolMetadata
                {
                    Name = tool.Name,
                    Risk = ToolRisk.Dynamic,
                    ApprovalMode = ToolApprovalMode.Conditional,
                    IsConcurrencySafe = false,
                    SearchHint = $"MCP tool: {tool.Description}",
                    // ToolMetadata.LoadPolicy 默认 Always，MCP 工具必须显式 Contextual：
                    // 本地小模型按关键词激活，避免 playwright 等多工具服务器全量撑爆上下文。
                    LoadPolicy = ToolLoadPolicy.Contextual,
                    // Description 并入 Keywords，让本地小模型靠语义命中（而非必须完整
                    // 说出 mcp__server__tool 全名）也能通过 ToolSearch 激活该工具。
                    Keywords = [tool.Name, "mcp", .. TokenizeDescription(tool.Description)],
                };
                Metadata.Register(metadata);
                _registeredMcpMetadata[tool.Name] = metadata;
            }
        }
    }

    /// <summary>
    /// 提取工具描述里的检索词：按非字母数字切分，保留 ≥3 字符的 token（小写去重），
    /// 过滤通用停用词，最多 8 个 —— Keywords 是精确命中信号，塞太多会稀释权重。
    /// </summary>
    internal static string[] TokenizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return [];

        return description
            .Split(DescriptionSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(static t => t.Trim().ToLowerInvariant())
            .Where(static t => t.Length >= 3 && !DescriptionStopWords.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxDescriptionKeywords)
            .ToArray();
    }

    private static readonly char[] DescriptionSeparators = [' ', '\t', '\n', '\r', ',', '.', ';', ':', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\', '_', '-'];

    private static readonly HashSet<string> DescriptionStopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "from", "that", "this", "into", "onto", "over",
        "get", "set", "list", "when", "then", "than", "all", "any", "use", "used",
        "using", "your", "our", "will", "can", "may", "tool", "mcp",
    };

    private const int MaxDescriptionKeywords = 8;

    /// <summary>Builds AIFunctions from registrations.</summary>
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

            tools.Add(function);
            metadata.Register(new ToolMetadata
            {
                Name = reg.Name,
                Aliases = reg.Aliases ?? [],
                Risk = reg.Risk,
                // ApprovalMode 已在注册入口解析为具体值（见 AddToolInstance），此处不再兜底推导。
                ApprovalMode = reg.ApprovalMode,
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

    private static AIFunction CreateFunction(IServiceProvider services, ToolRegistration reg) =>
        reg.FunctionFactory(services);
}
