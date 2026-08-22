using Microsoft.Agents.AI;

namespace OneCode.App.Services.PlanMode;

/// <summary>
/// Plan 模式增量提醒提供者——基于 MAF <see cref="AIContextProvider"/> 实现。
///
/// 职责分工（与 ModeInstructionProvider 的协作）：
/// - <b>每轮完整指令</b>：由 <c>ModeInstructionProvider</c> 承载
///   （在 <c>MainModeContextProviderBuilder.CreateModeInstructionProviderAsync</c> 中从 plan.prompt 加载）。
///   ModeInstructionProvider 是 OneCode 自有的模式指令注入器，替代原 MAF AgentModeProvider
///   （后者附带 mode_get/mode_set 工具，与宿主驱动的模式架构冲突）。
/// - <b>增量提醒（本类负责）</b>：本类补充模式相关动态状态提醒，
///   防止 LLM 在长对话中偏离 Plan 模式约束。
///
/// 注入策略（仅在第 2 轮及之后触发，Turn 1 完整指令已由 ModeInstructionProvider 注入）：
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
