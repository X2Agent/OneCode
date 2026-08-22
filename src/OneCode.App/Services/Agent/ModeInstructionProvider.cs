// MAAI001 suppressed: AIContextProvider uses experimental MAF APIs
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Services.Context;

namespace OneCode.App.Services.Agent;

/// <summary>
/// 模式指令注入 Provider——接管原 MAF <c>AgentModeProvider</c> 的指令注入职能。
///
/// <para>
/// 每次 invocation 注入当前工作模式（build/plan/team/goal）的完整系统提示词，
/// 行为对齐 AgentModeProvider 官方语义（"included in the instructions provided
/// to the agent on each invocation"）。与 AgentModeProvider 的区别：
/// 不注册 mode_get/mode_set 工具、不维护 MAF session state mode——
/// OneCode 的模式由宿主驱动（TUI 切换 WorkingMode/PermissionMode → 重建 agent），
/// LLM 驱动的 mode_set 切换不回写宿主状态，会造成指令与权限双轨不一致。
/// </para>
///
/// <para>
/// Turn 2+ 的模式相关增量提醒由 <see cref="ModeAwareAttachmentProviderBase"/>
/// 子类（Plan/BuildModeAttachmentProvider）的 sparse/full 交替负责，
/// 与本 Provider 的每轮完整指令注入互补。
/// </para>
/// </summary>
public sealed class ModeInstructionProvider(string instructions) : ReadOnlyAIContextProviderBase
{
    private readonly ChatMessage _instructionMessage = new(ChatRole.System, instructions);

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        AIContextProvider.InvokingContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new AIContext
        {
            Messages = [_instructionMessage],
        });
}
