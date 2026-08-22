using Microsoft.Agents.AI;
using OneCode.Core.Prompt;

namespace OneCode.App.Services.BuildMode;

/// <summary>
/// Build 模式增量提醒提供者——基于 MAF <see cref="AIContextProvider"/> 实现。
///
/// 职责分工（与 ModeInstructionProvider 的协作）：
/// - <b>每轮完整指令</b>：由 <c>ModeInstructionProvider</c> 承载
///   （在 <c>MainModeContextProviderBuilder.CreateModeInstructionProviderAsync</c> 中从 build.prompt 加载）。
///   ModeInstructionProvider 是 OneCode 自有的模式指令注入器，替代原 MAF AgentModeProvider
///   （后者附带 mode_get/mode_set 工具，与宿主驱动的模式架构冲突）。
/// - <b>增量提醒（本类负责）</b>：本类补充模式相关动态状态提醒，
///   防止 LLM 在长对话中偏离编码约束。
///
/// 注入策略（仅在第 2 轮及之后触发，Turn 1 完整指令已由 ModeInstructionProvider 注入）：
/// - Turn 2-4：注入简短提醒（sparse reminder），强调"先读再改、最小化变更、验证改动"。
/// - Turn 5/10/15…：再次注入完整工作流指令（full reminder），刷新 LLM 的方法论记忆。
/// - 其他 turn：简短提醒。
///
/// MAF 原生能力利用：
/// - 继承 <see cref="ModeAwareAttachmentProviderBase"/>（封装了 Turn 1 跳过 + full/sparse 交替 + 缓存的公共逻辑）
/// - 通过 <see cref="AIContext.Messages"/> 注入 System 消息（MAF 原生）
/// - 通过 <see cref="IPermissionModeProvider"/> 判断当前是否在 Build 模式
/// </summary>
public sealed class BuildModeAttachmentProvider : ModeAwareAttachmentProviderBase
{
    private readonly IPermissionModeProvider _modeProvider;
    private readonly IPromptManager _promptManager;

    public BuildModeAttachmentProvider(
        IPermissionModeProvider modeProvider,
        IPromptManager promptManager)
    {
        _modeProvider = modeProvider;
        _promptManager = promptManager;
    }

    protected override bool IsInMode => _modeProvider.CurrentMode == PermissionMode.AcceptEdits;

    protected override async Task<string> LoadFullInstructionsAsync(CancellationToken ct) =>
        // 与 Turn 1 加载行为对齐（MainAgentRunner.CreateAgentModeProviderAsync）：
        // 文件不可用直接抛异常，避免与 build.prompt 文件双源维护导致漂移。
        await _promptManager.GetPromptAsync("system/build", ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Build mode prompt 'system/build' not found in any IPromptManager store.");

    protected override string GetSparseReminder(int turnCount) =>
        $"[Build Mode — Turn {turnCount}] Continue executing. Read files before editing, keep changes minimal, and verify correctness after implementation.";
}
