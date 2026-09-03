using OneCode.App.Tools;

namespace OneCode.App.Services.PlanMode;

/// <summary>
/// Plan 执行协议文本的单一真相源，供 <see cref="PlanAgentRunDispatcher"/>（首跑指令）
/// 与 <see cref="OneCode.App.Services.Context.PlanExecutionContextProvider"/>（续跑上下文）共用。
/// 协议文本与工作流状态机强耦合（步骤全部终态后自动进入 Verifying、
/// <see cref="PlanToolNames.CompleteVerification"/> 的 fail-closed 语义），属机器契约
/// 而非可调优 prompt——按 App/AGENTS.md 不纳入 prompts/ 文件化覆盖体系，集中于此防漂移。
/// </summary>
public static class PlanExecutionProtocol
{
    /// <summary>常规执行指令：按依赖顺序从持久化步骤状态续跑。</summary>
    public const string ExecutionDirective =
        "Execute steps in dependency order. Resume from the persisted step states; do not repeat completed steps.";

    /// <summary>Verifying 态恢复指令：只允许补验证，不得改动实现步骤。</summary>
    public const string VerifyingRecoveryDirective =
        $"The persisted workflow is already in Verifying. Do not modify implementation steps. " +
        $"Run only the required build/tests/checks and call {PlanToolNames.CompleteVerification} with concrete evidence.";

    /// <summary>执行上下文（Turn 1 全量刷新）结尾的协议句。</summary>
    public const string ContextProtocolLine =
        $"Execute exactly this approved snapshot. Use {PlanToolNames.UpdateStep} for progress; " +
        "verification starts automatically once all steps are terminal, then call " +
        $"{PlanToolNames.CompleteVerification} with concrete evidence.";

    /// <summary>执行上下文（Turn 2+ 续跑）短句。</summary>
    public const string ContinueLine =
        "Continue executing the immutable approved snapshot. Persist step progress and " +
        "verification evidence through the plan execution tools.";

    /// <summary>首跑指令的 Required workflow protocol 块（四条协议，绑定本次 run id）。</summary>
    public static string BuildProtocolBlock(string runId) => $"""
        ## Required workflow protocol
        Active run id: {runId}
        1. Call {PlanToolNames.UpdateStep} for every state change. Completed steps require concrete evidence.
        2. When all steps are completed or explicitly skipped, the workflow automatically enters Verifying.
        3. Run the required build/tests/checks, then call {PlanToolNames.CompleteVerification} with command output as evidence.
        4. Do not claim completion unless {PlanToolNames.CompleteVerification} returns a completed workflow.
        """;
}