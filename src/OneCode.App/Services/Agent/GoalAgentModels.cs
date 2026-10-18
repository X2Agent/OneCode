using Microsoft.Extensions.AI;
using OneCode.Core.Goals;

namespace OneCode.App.Services.Agent;

/// <summary>
/// GOAL 模式运行选项。
/// </summary>
public sealed record GoalRunOptions
{
    public required string Goal { get; init; }

    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// 会话基础系统提示词（由 <c>system/default.prompt</c> 渲染，含 <c>AGENTS.md</c> 与 <c>Environment</c> 上下文）。
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>模型 ID（默认走主模型）。</summary>
    public string? ModelId { get; init; }

    /// <summary>每个子目标单次执行内的最大工具调用轮数。</summary>
    public int MaxTurnsPerSubGoal { get; init; } = 50;

    /// <summary>可用工具列表。</summary>
    public required IList<AITool> Tools { get; init; }

    /// <summary>
    /// 编排事件回调。当设置时，子目标执行期间的工具调用（ToolStart/ToolDone）
    /// 会通过此 sink 推送到 TUI，实现多步执行期间的实时进度透明度。
    /// 由 InteractiveModeExecutor 创建 Channel 并传入。
    /// </summary>
    public Action<OneCode.Core.Coordinator.OrchestrationEvent>? OrchestrationEventSink { get; init; }

    /// <summary>
    /// 三级预算（attempt + token + 时间）。
    /// 默认 null → Goal 工作流 Runtime 使用 <see cref="GoalBudget"/> 默认值。
    /// 100% 消耗时强制终止 + 保存 Checkpoint；70%/90% 触发 TUI 警告。
    /// </summary>
    public GoalBudget? Budget { get; init; }

    /// <summary>
    /// Optional image file paths for multimodal input. When non-empty, images are
    /// attached to the first user message as DataContent blocks.
    /// </summary>
    public IReadOnlyList<string>? ImagePaths { get; init; }
}

/// <summary>
/// 目标计划：包含分解后的子目标列表。
/// 使用 record 以支持 with 表达式。
/// </summary>
public sealed record GoalPlan
{
    public IReadOnlyList<GoalItem> Goals { get; init; } = [];
}

/// <summary>
/// 单个子目标。
/// 使用 record 以支持 with 表达式。
/// 注意：Description/SuccessCriteria/Status 仍为 set，因为执行过程中会修改状态。
/// </summary>
public sealed record GoalItem
{
    /// <summary>子目标序号（从 1 开始）。</summary>
    public int Id { get; init; }

    public string Description { get; set; } = "";

    /// <summary>成功标准（如何量化验证）。</summary>
    public string SuccessCriteria { get; set; } = "";

    public GoalStatus Status { get; set; } = GoalStatus.Pending;

    /// <summary>
    /// 子目标执行允许使用的工具名白名单。
    /// 为 null 或空时表示不裁剪（使用全量工具集）。
    /// 包含 "*" 时同样表示不裁剪。
    /// 来自 goal-decomposer.prompt 的 LLM 输出。
    /// </summary>
    public IReadOnlyList<string>? RequiredTools { get; set; }

    /// <summary>
    /// 子目标层级深度（0 = 根子目标，1 = 第一次按需分解的子目标，以此类推）。
    /// 默认 0，扁平分解场景无变化。Goal 工作流在执行时按需递归分解会逐层 +1。
    /// </summary>
    public int Depth { get; init; }

    /// <summary>
    /// 标记此子目标是否需要进一步分解（LLM 判断该子目标过大、应再次分解）。
    /// 默认 false。true 时 Goal 工作流在执行到此子目标前调用 GoalDecomposer 分解它，
    /// 把分解结果扁平插入到 GoalList 当前位置之后，再继续执行。
    /// 这是"按需递归分解"策略：避免预先分解所有层级（浪费 token），仅在需要时分解。
    /// </summary>
    public bool NeedsFurtherDecomposition { get; init; }

    /// <summary>
    /// 此子目标允许修改的工作区相对路径或目录。为空时仅允许修改工作目录内文件；
    /// 非空时进一步收窄到声明范围。
    /// </summary>
    public IReadOnlyList<string> AllowedPaths { get; init; } = [];

    /// <summary>是否要求执行项目构建。源码发生修改时即使为 false 也会自动要求构建。</summary>
    public bool RequiresBuild { get; init; }

    /// <summary>是否要求执行项目自动化测试。</summary>
    public bool RequiresTests { get; init; }

    /// <summary>是否为可选子目标。可选子目标失败不会阻止最终发布。</summary>
    public bool Optional { get; init; }

    /// <summary>
    /// 步内并行子任务（goal-decomposer.prompt 的 subTasks 契约）。非空时该子目标
    /// 经 <see cref="GoalSubTaskDagRunner"/> 以共享 DAG 底座并行执行，不走单代理循环。
    /// </summary>
    public IReadOnlyList<GoalSubTask>? SubTasks { get; set; }
}

/// <summary>
/// 子目标执行状态。
/// </summary>
public enum GoalStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Skipped,
}

/// <summary>单次工具执行的可审计证据。</summary>
public sealed record GoalToolExecutionEvidence(
    string ToolName,
    bool IsError,
    string? Result);

/// <summary>
/// GOAL 子目标执行链的确定性验证依赖束（硬门禁消费）。DI 单例，
/// 由 <see cref="GoalSubGoalExecutor"/> 与 <see cref="GoalSubTaskDagRunner"/> 共享。
/// </summary>
public sealed record GoalSubGoalGateDependencies(
    IVerificationProvider? VerificationProvider,
    OneCode.App.Services.Lsp.LspDiagnosticRegistry? DiagnosticRegistry);

/// <summary>确定性验证步骤的可审计证据。</summary>
public sealed record GoalValidationEvidence(
    string Gate,
    bool Passed,
    bool Skipped,
    string Summary);

/// <summary>
/// 子目标真实证据。AI Judge 只能在 HardValidationPassed 为 true 后运行，
/// 且其输入必须包含此证据摘要，不能只依赖 Agent 自述。
/// <paramref name="ChangedFiles"/> 统一保存工作区/隔离 worktree 内的规范化绝对路径，避免子目录相对化丢失父级目录偏移。
/// </summary>
public sealed record SubGoalEvidence(
    string AgentSummary,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<GoalToolExecutionEvidence> ToolExecutions,
    IReadOnlyList<GoalValidationEvidence> Validations,
    IReadOnlyList<string> Diagnostics)
{
    public bool HardValidationPassed => Validations.All(v => v.Passed || v.Skipped);
}

/// <summary>
/// 单个子目标执行结果。
/// 由 <see cref="GoalSubGoalExecutor"/> 产出，Goal 工作流 Runtime 和 <see cref="GoalDecomposer"/> 消费。
/// </summary>
public sealed record SubGoalExecution(
    int GoalId,
    GoalStatus Status,
    int Attempts,
    long InputTokens,
    long OutputTokens,
    string AgentOutput,
    string Evaluation,
    SubGoalEvidence? Evidence = null);
