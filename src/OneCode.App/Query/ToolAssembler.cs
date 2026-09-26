using Microsoft.Extensions.AI;
using OneCode.Core.Config;
using OneCode.Core.Models;

namespace OneCode.App.Query;

/// <summary>
/// Tool assembly / config resolution / unknown-tool fallback for <see cref="QueryStreamEngine"/>.
/// </summary>
internal sealed class ToolAssembler(
    IToolCatalog toolCatalog,
    IConfigManager configManager,
    ISessionToolSetManager sessionToolSetManager,
    IToolCapabilityResolver toolCapabilityResolver,
    ILogger logger)
{
    private readonly IToolCatalog _toolCatalog = toolCatalog;
    private readonly IConfigManager _configManager = configManager;
    private readonly ISessionToolSetManager _sessionToolSetManager = sessionToolSetManager;
    private readonly IToolCapabilityResolver _toolCapabilityResolver = toolCapabilityResolver;
    private readonly ILogger _logger = logger;

    /// <summary>
    /// 链路三：未知工具兜底。本地小模型经常凭名字 hallucinate 调用工具，
    /// 当前行为是直接报 "unknown tool"。改为——若该名字在注册表中存在但未加载，
    /// 自动激活并返回提示。这把最高频的失败模式变成了自愈路径。
    /// </summary>
    public void TryAutoActivateUnknownTool(string toolName, IReadOnlyList<AIFunction> localTools)
    {
        // 只处理在注册表中存在、但不在当前工具列表中的工具
        var meta = _toolCatalog.Metadata.Get(toolName);
        if (meta is null || !meta.IsVisible || !meta.IsEnabled)
            return;

        // 如果工具已在当前列表中，无需激活
        if (localTools.Any(t => t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase)))
            return;

        if (_sessionToolSetManager.TryActivate(toolName))
        {
            _logger.LogInformation(
                "Auto-activated tool '{ToolName}' via unknown-tool fallback (Chain 3). " +
                "It will be available in the next turn.",
                toolName);
        }
    }

    /// <summary>
    /// Builds the active tool list. For Ollama, uses session-level tool activation:
    /// Always tools + session-activated tools (monotonic growth, prompt-stable ordering).
    /// Cloud models receive the full catalog.
    /// </summary>
    /// <remarks>
    /// 「三条激活链路」设计说明见 <see cref="SessionToolSet"/> 的类级文档（单一权威来源）。
    /// </remarks>
    public IReadOnlyList<AIFunction> AssembleTools(
        string userPrompt,
        ToolCapabilitySet capabilities,
        SessionId? conversationId = null)
    {
        var allTools = _toolCatalog.Tools
            .Where(tool => capabilities.AllowedToolNames.Contains(tool.Name))
            .ToList();
        var provider = _configManager.Current.Effective.Provider?.ToLowerInvariant();

        // 使用 ModelCapabilities.RequiresToolFiltering 判断（provider 优先）：
        // Anthropic/OpenAI 等云端 provider 始终全量（prompt caching 更高效）；
        // provider == "ollama" 始终过滤（工具定义 token 开销对小窗口影响显著）；
        // 未知 provider 才按 ollamaContextWindow 阈值决定（≥ 32K 全量）。
        var contextWindow = _configManager.Current.Effective.OllamaContextWindow;
        var needsFiltering = ModelCapabilities.RequiresToolFiltering(provider, contextWindow);

        if (!needsFiltering)
        {
            _logger.LogDebug("Assembled {Total} tools (full catalog for provider={Provider}, contextWindow={ContextWindow})",
                allTools.Count, provider ?? "default", contextWindow);
            return allTools;
        }

        // Filtered path: session-level tool activation via SessionToolSet
        if (conversationId is { } convId)
        {
            var session = _sessionToolSetManager.GetOrCreate(convId.ToString());
            var selected = session.GetTools(userPrompt, capabilities);

            _logger.LogDebug("Filtered session tool selection: {Selected}/{Total} tools (activated: {Activated})",
                selected.Count, allTools.Count, session.ActivatedNames.Count);

            return selected;
        }

        // No session — return full catalog (e.g. UpdateCacheSafeParams before first query)
        _logger.LogDebug("Assembled {Total} tools (no session, full catalog)", allTools.Count);
        return allTools;
    }

    /// <summary>
    /// 从 <see cref="AppSettings.MaxTurns"/> 动态解析最大轮数。
    /// 支持运行时通过 /config 命令修改 maxTurns 后立即生效。
    /// </summary>
    public int ResolveMaxTurns() =>
        _configManager.Current.Effective.MaxTurns;

    /// <summary>
    /// 从 <see cref="AppSettings.MaxBudgetTokens"/> 动态解析预算上限。
    /// 支持运行时通过 /config 命令修改 maxBudgetTokens 后立即生效。
    /// </summary>
    public long? ResolveMaxBudgetTokens() =>
        _configManager.Current.Effective.MaxBudgetTokens;

    /// <summary>
    /// Snapshots the tool capabilities for the current working mode as the plan-approval tool policy.
    /// Resolved explicitly (not via ToolActivationContext.AsyncLocal) because the plan-approval gate runs
    /// inside an async iterator where ExecutionContext propagation across yields is not reliable.
    /// </summary>
    public IReadOnlyList<string> SnapshotApprovedTools()
        => _toolCapabilityResolver.Resolve(WorkingMode.Build).AllowedToolNames
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
