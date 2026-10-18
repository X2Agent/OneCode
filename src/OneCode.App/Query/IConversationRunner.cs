using OneCode.Core.Build;

namespace OneCode.App.Query;

/// <summary>
/// 受控工作流 run 的请求：由批准计划驱动（PlanWorkflow 审批卡批准后派发/恢复）。
/// </summary>
/// <remarks>
/// <paramref name="PrescribedBuildPlan"/> 是必填不变量：受控执行恒由上层批准的计划驱动，
/// BuildRun 层不再有"模型自产计划 + 流内审批"的交互门。
/// </remarks>
public sealed record WorkflowRunRequest(
    string RunId,
    SessionId SessionId,
    string Instruction,
    string SystemPrompt,
    string ModelId,
    WorkingMode WorkingMode,
    string? WorkingDirectory,
    BuildPlan PrescribedBuildPlan,
    string? HarnessInstructions = null);

/// <summary>
/// 把一段 prompt 当作用户输入提交给 LLM 跑一遍的契约。
/// </summary>
/// <remarks>
/// 交互式流式（TUI）与 headless 触发器（<see cref="OneCode.App.Services.Cron.CronJobExecutor"/>、
/// AutoDream）都只依赖本接口，不依赖具体实现：cron 只需要"跑一段 prompt"的能力，不需要
/// 会话持久化、hook 触发、token 统计等交互式职责。当前实现为 <see cref="QueryStreamEngine"/>；
/// 该接口同时是构造图保持 DAG 的前提——<c>ToolCatalog</c> 解析的工具实例（含 <c>CronTool</c>）
/// 间接依赖本接口的消费者，若实现方以具体类型被工具层反向引用，构造期 DI 将无限递归。
/// </remarks>
public interface IConversationRunner
{
    /// <summary>
    /// Streams one interactive query.
    /// </summary>
    /// <remarks>
    /// <paramref name="systemPrompt"/> carries the agent body (<c>system/default</c>) only, and
    /// <paramref name="harnessPrompt"/> the shared harness fragment (<c>system/harness</c>) — MAF
    /// composes the two via <c>HarnessInstructions</c>. A null <paramref name="harnessPrompt"/>
    /// leaves MAF's generic default instructions in place; passing the loaded fragment is what makes
    /// the product's security guidance reach the model.
    /// </remarks>
    IAsyncEnumerable<QueryEvent> StreamQueryAsync(
        string prompt,
        string systemPrompt,
        string modelId,
        int? thinkingBudget = null,
        CancellationToken ct = default,
        WorkingMode workingMode = WorkingMode.Build,
        Action<FileChange>? fileChangeCallback = null,
        IReadOnlyList<string>? imagePaths = null,
        string? harnessPrompt = null,
        bool unattended = false);

    IAsyncEnumerable<QueryEvent> StreamWorkflowRunAsync(
        WorkflowRunRequest request,
        CancellationToken ct = default);
}
