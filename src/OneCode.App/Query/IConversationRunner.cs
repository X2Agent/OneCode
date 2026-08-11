using OneCode.App.Tui;
using OneCode.Core.Build;

namespace OneCode.App.Query;

public sealed record WorkflowRunRequest(
    string RunId,
    SessionId SessionId,
    string Instruction,
    string SystemPrompt,
    string ModelId,
    WorkingMode WorkingMode,
    string? WorkingDirectory = null,
    BuildPlan? PrescribedBuildPlan = null);

/// <summary>
/// 把一段 prompt 当作用户输入提交给 LLM 跑一遍的契约。
/// </summary>
/// <remarks>
/// 本接口是断开循环依赖的两步之一：
/// <c>ChatService → ToolCatalog → CronCreateTool → CronSchedulerService
/// → ICronJobExecutor → CronJobExecutor → ChatService</c>。
/// <list type="number">
/// <item><b>本接口（解耦消费侧）</b>：<c>CronJobExecutor</c> 依赖 <see cref="IConversationRunner"/>
/// 而非 <c>ChatService</c> 具体类。cron 只需要"跑一段 prompt"的能力，不需要会话持久化、
/// hook 触发、token 统计等交互式职责。当前实现为 <c>ChatService</c>，未来可替换为更精简的
/// headless executor，只需改 DI 绑定。</item>
/// <item><b>ToolCatalog 惰性构建（消除构造期根因）</b>：环的根因是 <c>ToolCatalog</c> 构造期
/// 急切调用 <c>BuildStaticTools</c> 解析全部工具 POCO（含 <c>CronCreateTool</c>），递归回
/// <c>ChatService</c>。<c>ToolCatalog</c> 已改为首次访问 <c>Tools</c> 时按需构建，构造图变 DAG，
/// <c>ChatService</c> 得以恢复正常构造注入 <c>ToolCatalog</c>，无需 service locator。</item>
/// </list>
/// 两步缺一不可：单抽接口不改 ToolCatalog，构造期 DI 仍会无限递归；单 lazy 化不抽接口，
/// <c>CronJobExecutor</c> 仍耦合 <c>ChatService</c> 具体类且不可单测。
/// </remarks>
public interface IConversationRunner
{
    IAsyncEnumerable<QueryEvent> StreamQueryAsync(
        string prompt,
        string systemPrompt,
        string modelId,
        int? thinkingBudget = null,
        CancellationToken ct = default,
        WorkingMode workingMode = WorkingMode.Build,
        Action<FileChange>? fileChangeCallback = null,
        IReadOnlyList<string>? imagePaths = null);

    IAsyncEnumerable<QueryEvent> StreamWorkflowRunAsync(
        WorkflowRunRequest request,
        CancellationToken ct = default);
}
