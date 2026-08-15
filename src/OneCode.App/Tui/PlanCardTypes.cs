namespace OneCode.App.Tui;

/// <summary>Plan card decision from keyboard shortcut (design-spec §4.2).</summary>
public enum PlanCardDecision { Approve, Reject, Edit }

/// <summary>
/// Plan card lifecycle phase — distinguishes draft plans (still being refined
/// by the LLM via SavePlan) from plans awaiting user approval (submitted via SubmitPlan).
/// </summary>
public enum PlanCardPhase
{
    /// <summary>
    /// Phase 4 草稿——LLM 仍在通过 SavePlan 调用迭代修订计划。
    /// 卡片仅展示，不弹出决策面板，用户无法提前决策。
    /// </summary>
    Draft,

    /// <summary>
    /// SubmitPlan 已返回，但当前 Plan Run 尚未完成持久化和协议校验。
    /// 仅展示提交中状态，不开放审批。
    /// </summary>
    Finalizing,

    /// <summary>
    /// Plan Run 已完整闭合并通过协议校验，允许用户审批。
    /// </summary>
    PendingApproval,

    /// <summary>批准快照已冻结，正在幂等启动新的 Build Run。</summary>
    StartingExecution,

    /// <summary>Build Run 正在执行批准的结构化步骤。</summary>
    Executing,

    /// <summary>Build 工作已结束，正在验证验收条件。</summary>
    Verifying,

    /// <summary>全部步骤和验证门禁已通过。</summary>
    Completed,

    /// <summary>工作流执行或验证失败。</summary>
    Failed,

    /// <summary>工作流已取消。</summary>
    Cancelled,
}

/// <summary>
/// Plan card 持久化快照——通过 <see cref="Conversation.Metadata"/> 跨会话恢复。
/// 用于用户退出程序后重新打开会话时，恢复 plan card 的 Phase 状态（Draft/PendingApproval）
/// 和内容，使 InlineSelector 决策面板能继续工作。
/// </summary>
/// <remarks>
/// 设计说明：仅靠 plan.md 文件不足以恢复 PlanCardPhase 状态——plan.md 只含 plan 内容，
/// 不知道当前是 Draft（用户不应看到决策面板）还是 PendingApproval（应弹决策面板）。
/// 通过 Conversation.Metadata 持久化 Phase 字段是必要补充。
///
/// <para>
/// 参考 <c>AgentSessionStore.cs</c> 持久化 MAF AgentSession 的模式：
/// 序列化为 JSON 字符串存入 Metadata，反序列化时兼容 JsonElement 形态。
/// </para>
/// </remarks>
public sealed record PlanCardStateSnapshot(
    string Title,
    List<PlanStepSnapshot> Steps,
    PlanCardPhase Phase,
    DateTimeOffset SavedAt);

/// <summary>
/// <see cref="PlanStep"/> 的持久化 DTO——避免直接序列化 internal 类型。
/// 与 <see cref="PlanStepDto"/> 字段对齐，但用于持久化场景（跨会话恢复）。
/// </summary>
public sealed record PlanStepSnapshot(
    string Label,
    string? Content,
    string? Assignee,
    string Status);

/// <summary>
/// <see cref="Conversation.Metadata"/> 中持久化 PlanCardState 的 key 常量。
/// 命名风格参考 <c>MafSessionMetadataKey</c>（"mafSession"）——camelCase 短词。
/// </summary>
public static class PlanCardStateKeys
{
    /// <summary>
    /// Conversation.Metadata key——值为序列化的 <see cref="PlanCardStateSnapshot"/> JSON 字符串。
    /// 由 <c>CreatePlanTool.SavePlanAsync</c>（Draft）和 <c>SubmitPlanAsync</c>（PendingApproval）写入，
    /// 由 <c>TuiHostConfigurator.WireSessionModals</c> 在会话恢复时读取，
    /// 由 <c>TuiHostConfigurator.WirePlanCard</c> 在 Approve 决策时清除。
    /// </summary>
    public const string PlanCard = "planCard";
}

/// <summary>Mutable state for an active plan card awaiting user decision.</summary>
internal sealed class PlanCardState(
    string title,
    List<PlanStep> steps,
    PlanCardPhase phase,
    string? markdown = null)
{
    public string Title { get; } = title;
    public List<PlanStep> Steps { get; } = steps;
    public PlanCardPhase Phase { get; } = phase;
    public string? Markdown { get; } = markdown;
}

public enum PlanStepStatus { Pending = 0, Current = 1, Done = 2, }

/// <summary>
/// A single step in a plan card.
/// </summary>
/// <param name="Label">Short label shown as the primary line.</param>
/// <param name="Content">Optional longer description shown as a secondary line under the label.</param>
/// <param name="Assignee">Optional agent name shown as → name on the right.</param>
/// <param name="Status">Pending / Current / Done. Controls color and strikethrough.</param>
public sealed record PlanStep(
    string Label,
    string? Content = null,
    string? Assignee = null,
    PlanStepStatus Status = PlanStepStatus.Pending);
