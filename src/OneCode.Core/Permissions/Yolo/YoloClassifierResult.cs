namespace OneCode.Core.Permissions.Yolo;

/// <summary>
/// YOLO 分类器的判定结果。
///
/// 语义约定：
/// <list type="bullet">
///   <item><see cref="Allow"/>：放行（ShouldBlock=false）</item>
///   <item><see cref="Block"/>：硬拒绝（ShouldBlock=true, IsSoftDeny=false）→ 上层映射为 Deny</item>
///   <item><see cref="SoftDeny"/>：软拒绝（ShouldBlock=true, IsSoftDeny=true）→ 上层映射为 Ask（交人工确认）</item>
///   <item><see cref="None"/>：未匹配任何规则（ShouldBlock=false）→ 上层 fallback 到 AutoModePermissionStrategy</item>
/// </list>
///
/// None 语义：
/// 纯规则路径无法覆盖所有命令，未匹配时返回 None，
/// PermissionChecker 收到 None 后 fallback 到 AutoModePermissionStrategy
/// （走 EvaluateRules → 无规则则 Ask），保证安全兜底。
/// </summary>
public sealed record YoloClassifierResult(
    bool ShouldBlock,
    string Reason,
    string Model,
    string Stage,
    UserRule? MatchedRule = null,
    bool IsSoftDeny = false,
    bool IsMatched = true)
{
    public static YoloClassifierResult Allow(string model, string stage, UserRule? rule = null) =>
        new(false, "Allowed", model, stage, rule, IsSoftDeny: false, IsMatched: true);

    public static YoloClassifierResult Block(string reason, string model, string stage, UserRule? rule = null) =>
        new(true, reason, model, stage, rule, IsSoftDeny: false, IsMatched: true);

    public static YoloClassifierResult SoftDeny(string reason, string model, string stage, UserRule? rule = null) =>
        new(true, reason, model, stage, rule, IsSoftDeny: true, IsMatched: true);

    /// <summary>
    /// 未匹配任何规则。PermissionChecker 收到此结果后应 fallback 到 AutoModePermissionStrategy。
    /// </summary>
    public static YoloClassifierResult None() =>
        new(false, "No rule matched", "none", "fallback", IsMatched: false);
}
