using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OneCode.App.Query;
using OneCode.App.Services.PlanMode;
using OneCode.Core.PlanMode;

namespace OneCode.App.Services.Context;

/// <summary>
/// 批准计划的 Build run 活跃期间，每轮注入完整的 PLAN→BUILD 交接上下文（用户原始诉求 + 澄清问答 +
/// 不可变计划快照 + 执行规则）。持久化的工作流快照是唯一来源，可变的旧计划文件永不读取。
/// </summary>
/// <remarks>
/// 每轮全量注入而非首轮注入后只留「继续」短句：注入消息是 provider 的临时上下文、不进历史，
/// 压缩 / 恢复 / 续跑之后模型的历史里不会有计划全文；短句刷新只在计划恰好留在近窗口时才成立。
/// 计划应是自包含的短文档，每轮重复的成本可控，换来跨压缩与崩溃恢复的一致性。
/// </remarks>
public sealed class PlanExecutionContextProvider(
    IPlanWorkflowApplicationService workflowService,
    IPermissionModeProvider modeProvider)
    : ReadOnlyAIContextProviderBase
{
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken ct)
    {
        if (modeProvider.CurrentMode != PermissionMode.AcceptEdits)
            return new AIContext();

        if (SessionId.TryParse(ToolActivationContext.CurrentConversationId) is not { } sessionId)
            return new AIContext();

        var workflow = await workflowService.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (workflow?.ApprovedSnapshot is not { } snapshot
            || workflow.State is not (PlanWorkflowState.StartingExecution or PlanWorkflowState.Executing))
        {
            return new AIContext();
        }

        return new AIContext
        {
            Messages = [new ChatMessage(ChatRole.System, PlanExecutionProtocol.BuildHandoffContext(snapshot))],
        };
    }
}
