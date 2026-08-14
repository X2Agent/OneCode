using Microsoft.Extensions.AI;
using OneCode.App.Tui;
using OneCode.Core.Build;
using OneCode.Core.Models;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Query;

/// <summary>
/// Tool assembly / config resolution / user-message / observability helpers for
/// <see cref="QueryStreamEngine"/>. Pure helpers only (no shared mutable state),
/// which is why a partial split is legitimate here — contrast with
/// <see cref="StreamingSession"/>, the state object required to break the
/// async-iterator boundary (ADR 0006).
/// </summary>
internal sealed partial class QueryStreamEngine
{
    /// <summary>
    /// Builds a user <see cref="ChatMessage"/>. When <paramref name="imagePaths"/> is provided,
    /// constructs a multi-content message with text + image <see cref="DataContent"/> blocks
    /// following the MAF multimodal pattern.
    /// </summary>
    internal static ChatMessage BuildUserMessage(
        string prompt,
        IReadOnlyList<string>? imagePaths,
        ILogger logger)
    {
        if (imagePaths is not { Count: > 0 })
            return new ChatMessage(ChatRole.User, prompt);

        var contents = new List<AIContent>();
        if (!string.IsNullOrEmpty(prompt))
            contents.Add(new TextContent(prompt));

        foreach (var path in imagePaths)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var mediaType = ext switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    ".bmp" => "image/bmp",
                    _ => "image/png",
                };
                contents.Add(new DataContent(bytes, mediaType));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read image {Path}", path);
                contents.Add(new TextContent($"[Failed to load image: {Path.GetFileName(path)}]"));
            }
        }

        return new ChatMessage(ChatRole.User, contents);
    }

    /// <summary>
    /// 链路三：未知工具兜底。本地小模型经常凭名字 hallucinate 调用工具，
    /// 当前行为是直接报 "unknown tool"。改为——若该名字在注册表中存在但未加载，
    /// 自动激活并返回提示。这把最高频的失败模式变成了自愈路径。
    /// </summary>
    private void TryAutoActivateUnknownTool(string toolName, IReadOnlyList<AIFunction> localTools)
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
    private IReadOnlyList<AIFunction> AssembleTools(
        string userPrompt,
        ToolCapabilitySet capabilities,
        SessionId? conversationId = null)
    {
        var allTools = _toolCatalog.Tools
            .Where(tool => capabilities.AllowedToolNames.Contains(tool.Name))
            .ToList();
        var provider = _configManager.Current.Effective.Provider?.ToLowerInvariant();

        // P3: 使用 ModelCapabilities.RequiresToolFiltering 替代 provider == "ollama" 一刀切
        // 云端模型（Anthropic/OpenAI 等）始终全量；本地模型按上下文窗口决定：
        // ≥ 32K 走全量（prompt caching 更高效），< 32K 走过滤（SessionToolSet 分层加载）
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
    private int ResolveMaxTurns() =>
        _configManager.Current.Effective.MaxTurns;

    /// <summary>
    /// 从 <see cref="AppSettings.MaxBudgetUsd"/> 动态解析预算上限。
    /// 支持运行时通过 /config 命令修改 maxBudgetUsd 后立即生效。
    /// </summary>
    /// <remarks>
    /// <see cref="AppSettings.MaxBudgetUsd"/> 是 <c>double</c>，
    /// <c>MainAgentRunOptions.MaxBudgetUsd</c> 是 <c>decimal?</c>，需显式转换。
    /// </remarks>
    private decimal? ResolveMaxBudgetUsd() =>
        (decimal?)_configManager.Current.Effective.MaxBudgetUsd;

    /// <summary>
    /// Snapshots the tool capabilities for the current working mode as the plan-approval tool policy.
    /// Resolved explicitly (not via ToolActivationContext.AsyncLocal) because the plan-approval gate runs
    /// inside an async iterator where ExecutionContext propagation across yields is not reliable.
    /// </summary>
    private IReadOnlyList<string> SnapshotApprovedTools()
        => _toolCapabilityResolver.Resolve(WorkingMode.Build).AllowedToolNames
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string BuildPlanApprovalPrompt(
        BuildRun buildRun,
        IReadOnlyList<string> approvedTools)
    {
        var planSummary = buildRun.Plan?.Summary ?? "（无计划摘要）";
        var toolSummary = approvedTools.Count == 0
            ? "（空）"
            : string.Join(", ", approvedTools);
        return $"{planSummary}\n\n本次执行允许工具：{toolSummary}";
    }

    private void UpdateCacheSafeParams(QueryStreamRequest request, IReadOnlyList<AIFunction> localTools)
    {
        // 使用 localTools 的冻结快照——子代理通过 CacheSafeParams.Tools 获取工具列表，
        // 必须是独立副本而非共享可变引用（SessionToolSet 在后续轮次可能继续追加工具）。
        var toolList = localTools.Cast<AITool>().ToList();

        LastCacheSafeParams = new CacheSafeParams
        {
            SystemPrompt = request.SystemPrompt,
            ModelId = request.ModelId,
            ThinkingBudget = request.ThinkingBudget,
            Tools = toolList.Count > 0 ? toolList : null,
            ToolCapabilities = ToolActivationContext.CurrentCapabilities,
            Metadata = new Dictionary<string, object?> { ["turn"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
        };
    }

    private async Task FireHookAsync(HookEvent @event, SessionId? sessionId, string? workingDirectory,
        CancellationToken ct = default)
    {
        var payload = new HookPayload
        {
            Event = @event,
            SessionId = sessionId?.ToString(),
            Cwd = workingDirectory ?? Environment.CurrentDirectory,
        };

        await _hookExecutionService.FireAsync(payload, ct: ct);
    }

    private async Task NotifyAsync(string title, string message, CancellationToken ct)
    {
        // 配置开关：默认关闭，用户需显式开启 notificationsEnabled=true 才发桌面通知
        if (!_configManager.Current.Effective.NotificationsEnabled) return;
        if (!_notifierService.IsSupported) return;
        await _notifierService.SendNotificationAsync(title, message, ct).ConfigureAwait(false);
    }
}
