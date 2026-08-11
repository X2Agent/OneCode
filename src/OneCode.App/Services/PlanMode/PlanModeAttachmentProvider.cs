using Microsoft.Agents.AI;

namespace OneCode.App.Services.PlanMode;

/// <summary>
/// Plan 模式增量提醒提供者——基于 MAF <see cref="AIContextProvider"/> 实现。
///
/// 职责分工（与 MAF 原生能力的协作）：
/// - <b>Turn 1 完整指令</b>：由 MAF 原生 <c>AgentModeProvider.Instructions</c> 承载
///   （在 <c>MainAgentRunner.CreateAgentModeProviderAsync</c> 中从 plan.prompt 加载）。
///   AgentModeProvider 是 MAF 原生的模式感知机制，支持 DefaultMode/Modes/Instructions。
/// - <b>增量提醒（本类负责）</b>：MAF 的 AgentModeProvider 不支持每轮动态更新 Instructions，
///   本类补充这一能力，防止 LLM 在长对话中偏离 Plan 模式约束。
///
/// 注入策略（仅在第 2 轮及之后触发，Turn 1 由 AgentModeProvider 负责）：
/// - Turn 2-4：注入简短提醒（sparse reminder），防止 LLM "忘记"自己在 Plan 模式。
/// - Turn 5/10/15…：再次注入完整工作流指令（full reminder），刷新 LLM 的工作流记忆。
/// - 其他 turn：简短提醒。
///
/// MAF 原生能力利用：
/// - 继承 <see cref="ModeAwareAttachmentProviderBase"/>（封装了 Turn 1 跳过 + full/sparse 交替 + 缓存的公共逻辑）
/// - 通过 <see cref="AIContext.Messages"/> 注入 System 消息（MAF 原生）
/// - 通过 <see cref="IPlanModeService.IsInPlanMode"/> 判断当前是否在 Plan 模式
/// </summary>
public sealed class PlanModeAttachmentProvider : ModeAwareAttachmentProviderBase
{
    private readonly IPlanModeService _planMode;

    public PlanModeAttachmentProvider(IPlanModeService planMode)
    {
        _planMode = planMode;
    }

    protected override bool IsInMode => _planMode.IsInPlanMode;

    protected override async Task<string> LoadFullInstructionsAsync(CancellationToken ct) =>
        await _planMode.GetWorkflowInstructionsAsync(ct).ConfigureAwait(false) ?? "";

    protected override string GetSparseReminder(int turnCount) =>
        $"[Plan Mode — Turn {turnCount}] Continue planning. Do NOT execute tools that modify files.";
}
