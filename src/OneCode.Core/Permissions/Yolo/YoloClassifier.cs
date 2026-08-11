namespace OneCode.Core.Permissions.Yolo;

/// <summary>
/// YOLO 安全分类器——Auto 模式下的权限判定组件。
///
/// 纯规则路径（无 LLM 调用）：
///   1. ToolMetadataRegistry 驱动的安全工具短路（ApprovalMode.Never → 自动放行）
///   2. YoloRuleStore 规则匹配（内置 deny + allow + 用户自定义规则）
///   3. 未匹配 → 返回 None，由 PermissionChecker fallback 到 Auto 模式 ReadOnlyAndEvaluate
///      （走 EvaluateRules → 无规则则 Ask，保证安全兜底）
///
/// 保留本类的目的：
///   - 维持 YoloRuleStore 的调用封装（PermissionChecker 不直接依赖 YoloRuleStore）
///   - 通过 ToolMetadataRegistry 统一安全工具判定（不再维护独立的硬编码白名单）
/// </summary>
public sealed class YoloClassifier : IYoloClassifier
{
    private readonly YoloRuleStore _ruleStore;
    private readonly ILogger<YoloClassifier>? _logger;
    private readonly Tools.ToolMetadataRegistry _toolMetadata;

    public YoloClassifier(
        YoloRuleStore ruleStore,
        Tools.ToolMetadataRegistry toolMetadata,
        ILogger<YoloClassifier>? logger = null)
    {
        _ruleStore = ruleStore;
        _logger = logger;
        _toolMetadata = toolMetadata;
    }

    /// <summary>
    /// 对工具调用进行安全分类。
    /// 纯规则路径：allowlist → YoloRuleStore 规则匹配 → 未匹配返回 None。
    /// 不再调用 LLM——未匹配的命令由 PermissionChecker fallback 到 Auto 模式 ReadOnlyAndEvaluate。
    /// </summary>
    public Task<YoloClassifierResult> ClassifyAsync(
        string toolName,
        JsonElement toolInput,
        CancellationToken ct = default)
    {
        if (IsAllowlistedTool(toolName))
            return Task.FromResult(YoloClassifierResult.Allow("allowlist", "skip"));

        var inputString = ExtractInputString(toolName, toolInput);

        var ruleMatch = _ruleStore.MatchRule(inputString ?? toolName);
        if (ruleMatch != null)
        {
            _logger?.LogDebug("YOLO rule matched: {Type} {Pattern}", ruleMatch.Type, ruleMatch.Pattern);

            return Task.FromResult(ruleMatch.Type.ToLowerInvariant() switch
            {
                "allow" => YoloClassifierResult.Allow("user-rule", "rule", ruleMatch),
                "deny" => YoloClassifierResult.Block($"Blocked by rule: {ruleMatch.Description}", "user-rule", "rule", ruleMatch),
                "soft_deny" => YoloClassifierResult.SoftDeny($"Soft denied by rule: {ruleMatch.Description}", "user-rule", "rule", ruleMatch),
                _ => YoloClassifierResult.Block($"Unknown rule type: {ruleMatch.Type}", "user-rule", "rule", ruleMatch),
            });
        }

        // 未匹配任何规则 → 返回 None，PermissionChecker 将 fallback 到 AutoModePermissionStrategy
        // （走 EvaluateRules → 无规则则 Ask，保证安全兜底）
        return Task.FromResult(YoloClassifierResult.None());
    }

    public bool IsAllowlistedTool(string toolName) =>
        _toolMetadata.GetPolicy(toolName).ApprovalMode == Tools.ToolApprovalMode.Never;

    private static string? ExtractInputString(string toolName, JsonElement input)
    {
        // 对非对象 JsonElement 返回 null，让调用方通过 (inputString ?? toolName)
        // fallback 到 toolName 做规则匹配。
        if (input.ValueKind != JsonValueKind.Object)
            return null;

        // 委托给统一的 ToolArgumentExtractor 提取参数。
        var extracted = OneCode.Core.Tools.ToolArgumentExtractor.ExtractInputString(toolName, input);
        if (extracted is not null)
            return extracted;

        // 对象但无已知字段 → 返回整个 JSON 文本，让规则可对原始入参做正则匹配。
        return input.GetRawText();
    }
}
