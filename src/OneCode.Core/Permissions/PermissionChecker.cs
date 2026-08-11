using OneCode.Core.Permissions.Yolo;

namespace OneCode.Core.Permissions;

/// <summary>
/// 主权限检查器。Auto 模式下委托给 <see cref="IYoloClassifier"/> 做规则匹配，
/// 未匹配规则时 fallback 到 Auto 模式的 ReadOnlyAndEvaluate 逻辑；
/// 其余模式直接查表 <see cref="PermissionProfiles"/>。
///
/// Auto 模式判定流程：
/// 1. 只读工具 / 只读 Shell → Allow（不消耗 LLM，短路）
/// 2. 文件写入工具 → 路径校验后 Allow（不消耗 LLM，短路）
/// 3. 其他工具 → YoloClassifier 规则匹配
///    - 命中 allow 规则 → Allow
///    - 命中 deny 规则 → Deny
///    - 命中 soft_deny 规则 → Ask（交人工确认）
///    - 未命中（None）→ fallback 到 Auto 模式的 ReadOnlyAndEvaluate 配置
///      （走 EvaluateRules → 无规则则 Ask，保证安全兜底）
/// </summary>
public sealed class PermissionChecker : IPermissionChecker
{
    private readonly IYoloClassifier _yoloClassifier;
    private readonly ILogger<PermissionChecker>? _logger;

    public PermissionChecker(
        IYoloClassifier yoloClassifier,
        ILogger<PermissionChecker>? logger = null)
    {
        _yoloClassifier = yoloClassifier;
        _logger = logger;
    }

    public async Task<PermissionCheckResult> CheckAsync(
        string toolName,
        JsonElement toolInput,
        ToolPermissionContext context,
        CancellationToken ct = default)
    {
        if (context.Mode == PermissionMode.Auto)
            return await CheckAutoModeWithRulesAsync(toolName, toolInput, context, ct).ConfigureAwait(false);

        return PermissionProfiles.Check(context.Mode, toolName, toolInput, context);
    }

    private async Task<PermissionCheckResult> CheckAutoModeWithRulesAsync(
        string toolName, JsonElement toolInput, ToolPermissionContext context, CancellationToken ct)
    {
        var shortcut = PermissionCheckHelpers.CheckReadOnlyAndFileWrite(toolName, toolInput, context);
        if (shortcut != null)
            return shortcut;

        var yoloResult = await _yoloClassifier.ClassifyAsync(
            toolName, toolInput, ct: ct).ConfigureAwait(false);

        if (yoloResult.IsMatched)
        {
            if (!yoloResult.ShouldBlock)
                return PermissionCheckResult.Allow;

            if (yoloResult.IsSoftDeny)
            {
                _logger?.LogInformation(
                    "YOLO rule soft-denied tool {Tool}: {Reason}",
                    toolName, yoloResult.Reason);
                return PermissionCheckResult.Ask(
                    $"YOLO classifier requests confirmation: {yoloResult.Reason}");
            }

            _logger?.LogInformation(
                "YOLO rule blocked tool {Tool}: {Reason}",
                toolName, yoloResult.Reason);
            return PermissionCheckResult.Deny($"Blocked by YOLO classifier: {yoloResult.Reason}");
        }

        return PermissionProfiles.Check(PermissionMode.Auto, toolName, toolInput, context);
    }
}
